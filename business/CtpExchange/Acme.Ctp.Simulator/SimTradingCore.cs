using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Simulator.Storage;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 单个投资者（账户）的全部仿真状态：资金 + 持仓 + 报单 + 报单号序列。
/// </summary>
public sealed class SimAccountState
{
    internal SimAccountState(SimAccount account)
    {
        Account = account;
    }

    /// <summary>登录用的投资者账号。</summary>
    public string AccountId => Account.AccountId;

    public SimAccount Account { get; }
    public SimPositionManager Positions { get; } = new();
    public SimOrderManager Orders { get; } = new();

    /// <summary>下一个报单序号（登录时回给客户端作 <c>MaxOrderRef</c>）。</summary>
    internal int NextOrderRefSeq { get; set; }
}

/// <summary>
/// 本地交易仿真柜台核心（**与传输方式无关**）。
/// <para>
/// 职责：账户/持仓/报单状态 + 风控 + 订单簿撮合（价格发现归簿——行情 = 簿顶 + 成交流，事件驱动）+ 落盘。
/// Phase 1 曾由进程内 <c>SimCtpSession : ICtpSession</c> 直接托管；现为独立 host 进程
/// （<c>Acme.Ctp.Sim.Host</c>）持有本核心、经 <c>SimRpcServer</c> 服务多个 RPC 连接（核心零改动）。
/// </para>
/// <para>
/// <b>线程模型</b>：全部状态变更在 <c>lock (_gate)</c> 内串行化（仿真不是性能敏感路径，
/// 单锁比细粒度锁更不容易写错）；<b>事件在锁外触发</b>——回调可能重入核心
/// （客户端在 <c>OrderReceived</c> 里立刻下单是合法用法），持锁触发会自死锁。
/// </para>
/// <para>
/// <b>落盘是尽力而为</b>：写盘在锁外 fire-and-forget，失败只 <c>Trace</c>。
/// 仿真柜台是开发工具，磁盘故障不该拒单（与真柜台把失联当致命错误相反）。
/// 需要确定性时（单测）用 <see cref="FlushAsync"/> 等待挂起写盘。
/// </para>
/// </summary>
public sealed class SimTradingCore : IDisposable
{
    private readonly SimOptions _options;
    private readonly SimClock _clock;

    /// <summary>当前交易日（M3 行情腿登录应答/快照 tick 用——无 core.Login 语境的会话取日）。</summary>
    public string CurrentTradingDay => _clock.TradingDay;

    /// <summary>当前时间 HH:mm:ss（M3 快照 tick 的 UpdateTime）。</summary>
    public string CurrentTimeOfDay => _clock.TimeOfDay;
    private readonly IRiskEngine _risk;
    private readonly ISimStore _store;
    private readonly object _gate = new();

    private readonly Dictionary<string, SimAccountState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimInstrument> _instruments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _pendingWrites = new();

    /// <summary>合约订单簿（客户端互为对手层——价格发现的唯一来源）。全 mutation 在 <see cref="_gate"/> 内。</summary>
    private readonly Dictionary<string, OrderBook> _books = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上市合约表（host 上架清单——wire <c>queryInstruments</c> 的数据源；与全量表 <see cref="QueryInstruments"/> 区分）。</summary>
    private readonly List<SimInstrument> _listedBoard = new();

    /// <summary>上市合约 id 集（可交易/可订阅闸门——上市表 = 唯一入口）。</summary>
    private readonly HashSet<string> _listedIds = new(StringComparer.OrdinalIgnoreCase);

    private int _frontId = 1;
    private int _sessionId;

    public SimTradingCore(
        SimOptions options,
        SimClock? clock = null,
        IRiskEngine? risk = null,
        ISimStore? store = null)
    {
        _options = options;
        _clock = clock ?? new SimClock();
        _risk = risk ?? new SimRiskEngine(options);
        _store = store ?? StoreOf(options);

        Market = new SimMarketDataEngine(_clock, InstrumentOf);
        Market.TickGenerated += (_, tick) => TickProduced?.Invoke(tick);
        Market.BindBook(BookTopSnapshot, OpenInterestOf);   // 回调全部在 _gate 内调用（tick 发射点全在锁内）
    }

    /// <summary>
    /// 未显式注入存储时按 <see cref="SimOptions.DatabasePath"/> 决定落盘去向。
    /// <para>
    /// <b>为什么必须有这条</b>：宿主（<c>SimHostOptions.FromEnvironment</c> → <c>SimTradingCore(options)</c>）
    /// 只传 options 不传 store，若此处不回看 <c>DatabasePath</c>，则 <c>AETHER_CTP_SIM_DB</c> 全程形同虚设、
    /// 仿真柜台永不落盘，「重启续用账本」这条设计目标静默失效。
    /// </para>
    /// </summary>
    private static ISimStore StoreOf(SimOptions options)
        => string.IsNullOrWhiteSpace(options.DatabasePath) ? NullSimStore.Instance : SimStore.ForFile(options.DatabasePath!);

    /// <summary>行情引擎（价格发现归簿：bid/ask = 簿顶、last = 最新成交价、tick 事件驱动）。</summary>
    public SimMarketDataEngine Market { get; }

    /// <summary>报单状态变化（含状态推进与新增成交的双推次序）。<c>accountId</c> 由核心补齐。</summary>
    public event Action<string, CtpOrder>? OrderChanged;

    /// <summary>成交回报。</summary>
    public event Action<string, CtpTrade>? TradeFilled;

    /// <summary>合成 tick（恒产生——<c>Market.TickGenerated</c> 无条件转发，host 按订阅连接路由推送）。</summary>
    public event Action<CtpTick>? TickProduced;

    // —— 生命周期 ——

    /// <summary>建表 + 恢复上次运行的账户/持仓/合约表（首次运行播种内置合约表）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _store.InitializeAsync(ct).ConfigureAwait(false);

        var instruments = await _store.LoadInstrumentsAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var i in instruments) _instruments[i.InstrumentId] = i;

            if (_instruments.Count == 0)
            {
                foreach (var seed in InstrumentCatalog.Seed()) _instruments[seed.InstrumentId] = seed;
                foreach (var seed in _instruments.Values.ToList()) Persist(() => _store.SaveInstrumentAsync(seed, ct));
            }

            // 上市合约表（host 上架清单）：null/空 ⇒ 默认 = zx001（本交易所自有品种——「我就是交易所」；
            // 2026-09-27 起从种子 9 合约改为单合约）。价格源 = 订单簿价格发现（挂单/成交驱动）。
            // 自定义 id 经解析链（精确→品种派生→合成）落表；种子 9 合约仍可用 AETHER_CTP_SIM_BOARD 上架。
            var listedIds = _options.ListedInstruments is { Count: > 0 }
                ? _options.ListedInstruments
                : new[] { "zx001" };
            foreach (var id in listedIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ResolveMarketInstrument(id);
                var meta = InstrumentOf(id);
                if (meta is not null)
                {
                    _listedBoard.Add(meta);
                    _listedIds.Add(id);
                }
            }
        }

        var accounts = await _store.LoadAccountsAsync(ct).ConfigureAwait(false);
        foreach (var snapshot in accounts)
        {
            var positions = await _store.LoadPositionsAsync(snapshot.AccountId, ct).ConfigureAwait(false);
            var orders = await _store.LoadOrdersAsync(snapshot.AccountId, ct).ConfigureAwait(false);
            var tradeIds = await _store.LoadTradeIdsAsync(snapshot.AccountId, ct).ConfigureAwait(false);

            lock (_gate)
            {
                // 重启后**没有挂单存活**（冻结额不可精确恢复）——把上次的非终态报单标记为
                // 「未成交不在队列」并清零账户上的冻结额，保证资金守恒不留悬空冻结。
                var restored = SimAccount.FromSnapshot(snapshot);
                restored.ReleaseForOrder(snapshot.FrozenMargin, snapshot.FrozenCash);

                var state = new SimAccountState(restored);
                state.Positions.Restore(positions);

                var maxTradeId = MaxTradeId(tradeIds);
                foreach (var order in orders)
                {
                    // 单号序列必须续上：否则重启后第一张单重新拿到 "000000000001"，
                    // 与已恢复的历史报单**同号**——Accept 会覆盖历史单并把键插进插入序两遍
                    // （同一笔报单在查询里回两行），且客户端侧的单号也重号。
                    state.NextOrderRefSeq = Math.Max(state.NextOrderRefSeq, ParseOrderRefSeq(order.OrderRef));
                    state.Orders.Restore(SimOrderStatus.IsTerminal(order.OrderStatusRaw)
                        ? order
                        : order with
                        {
                            VolumeTotal = 0,
                            OrderStatusRaw = SimOrderStatus.NoTradeNotQueueing,
                            StatusMsg = "重启后未成交挂单已作废",
                        }, maxTradeId);
                }
                _states[snapshot.AccountId] = state;
            }
        }

        // 重启恢复行情会话统计（2026-09-28 tick 落盘的读侧）：从最后一条成交 tick 续上最新价/累计量/开高低——
        // 不归零回昨结算基准（成交是真实交易消息，跨重启延续）。
        // 恢复先于 RecomputePositionProfit——持仓浮盈按恢复后的最新价盯市。
        List<SimInstrument> knownInstruments;
        lock (_gate) knownInstruments = _instruments.Values.ToList();
        foreach (var instrument in knownInstruments)
        {
            try
            {
                var last = await _store.LoadRecentTicksAsync(instrument.InstrumentId, 1, ct).ConfigureAwait(false);
                if (last.Count > 0) lock (_gate) Market.Restore(instrument.InstrumentId, last[0]);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[CTP-SIM] 行情恢复读取失败（忽略）：{ex.Message}");
            }
        }

        RecomputePositionProfit();
    }

    /// <summary>释放（幂等）。行情事件驱动（无 feed 定时器可停）——仅同步 flush 落盘。</summary>
    public void Dispose()
    {
        FlushAsync().GetAwaiter().GetResult();
    }

    /// <summary>等待挂起的落盘任务完成（单测确定性用；生产路径不调）。</summary>
    public async Task FlushAsync()
    {
        Task[] pending;
        lock (_gate) pending = _pendingWrites.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length == 0) return;
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception ex) { Trace.WriteLine($"[CTP-SIM] 落盘刷新异常：{ex.Message}"); }
    }

    // —— 登录 / 登出 ——

    /// <summary>
    /// 仿真登录（账号系统：自助开户 + 密码认证——**密码必填**）：
    /// <list type="bullet">
    /// <item><b>密码必填</b>（2026-09-28 用户拍板：不能不填——免认证路径关闭，账户从出生即受保护）：
    /// 空/空白密码 ⇒ <see cref="SimErrorCodes.BadCredentials"/>，开新户也不例外。</item>
    /// <item><b>首次登录 = 开户</b>：密码入档（PBKDF2 哈希）。</item>
    /// <item><b>已设密码的账户</b>：密码必须匹配——不匹配抛 <see cref="SimErrorCodes.BadCredentials"/>。</item>
    /// <item><b>存量无密码账户</b>（升级前的库）：首次带密码登录 = 补设密码（平滑迁移——这是无密码账户唯一的存在来源）。</item>
    /// <item><b>防爆破</b>：连续 <see cref="SimOptions.LoginMaxFailures"/> 次失败锁定
    /// <see cref="SimOptions.LoginLockoutSeconds"/> 秒（按 <see cref="SimClock"/> 计——可测），锁定期内登录即拒。</item>
    /// <item><paramref name="brokerId"/> 记为账户元数据（身份键仍是 userId——单一交易所内经纪商是形式字段）。</item>
    /// </list>
    /// 成功返回可被客户端 <c>int.Parse</c> 的 <c>MaxOrderRef</c>。
    /// </summary>
    public CtpLoginResult Login(string brokerId, string userId, string? password = null)
    {
        var accountId = AccountIdOf(userId);
        lock (_gate)
        {
            // 防爆破锁定检查（先于任何账户操作）
            if (_loginFailures.TryGetValue(accountId, out var f) && _clock.Now < f.LockedUntil)
            {
                var remain = (int)Math.Ceiling((f.LockedUntil - _clock.Now).TotalSeconds);
                throw new CtpBusinessException(SimErrorCodes.BadCredentials,
                    $"账户 {accountId} 登录失败次数过多，已锁定（剩余 {remain} 秒）。");
            }

            // 密码必填——空/空白一律拒（开新户也不例外；账户从出生即受保护）
            if (string.IsNullOrWhiteSpace(password))
                throw new CtpBusinessException(SimErrorCodes.BadCredentials, "密码不能为空（所有账户必须设密码）。");

            _sessionId = _sessionId == 0 ? 1 : _sessionId;
            var state = Find(accountId);
            if (state is null)
            {
                state = GetOrCreate(accountId);   // 首次登录 = 开户
                state.Account.PasswordHash = HashPassword(password);
            }
            else if (!string.IsNullOrEmpty(state.Account.PasswordHash))
            {
                if (!VerifyPassword(password, state.Account.PasswordHash))
                {
                    RegisterLoginFailure(accountId);
                    throw new CtpBusinessException(SimErrorCodes.BadCredentials,
                        $"账户 {accountId} 凭据不匹配。");
                }
            }
            else
            {
                state.Account.PasswordHash = HashPassword(password);   // 存量无密码账户：带密首登补设（迁移路径）
            }

            _loginFailures.Remove(accountId);   // 成功 ⇒ 清失败计数
            state.Account.BrokerId = brokerId;

            // MaxOrderRef 必须是可解析的整数：CtpTrader.LoginAsync 拿它做下一单号起点
            var maxOrderRef = state.NextOrderRefSeq.ToString(CultureInfo.InvariantCulture);
            var result = new CtpLoginResult(_clock.TradingDay, _clock.TimeOfDay, _frontId, _sessionId, maxOrderRef);

            Persist(() => _store.SaveAccountAsync(state.Account.ToSnapshot()));
            return result;
        }
    }

    /// <summary>连续失败计数 + 锁定（内存态——重启清零；按 <see cref="SimClock"/> 计时可测）。</summary>
    private readonly Dictionary<string, (int Failures, DateTime LockedUntil)> _loginFailures = new(StringComparer.OrdinalIgnoreCase);

    private void RegisterLoginFailure(string accountId)
    {
        var failures = (_loginFailures.TryGetValue(accountId, out var f) ? f.Failures : 0) + 1;
        if (failures >= Math.Max(1, _options.LoginMaxFailures))
        {
            _loginFailures[accountId] = (0, _clock.Now.AddSeconds(Math.Max(0, _options.LoginLockoutSeconds)));
            Trace.WriteLine($"[CTP-SIM] 账户 {accountId} 连续登录失败 {failures} 次，锁定 {_options.LoginLockoutSeconds} 秒。");
        }
        else
        {
            _loginFailures[accountId] = (failures, DateTime.MinValue);
        }
    }

    /// <summary>密码哈希（PBKDF2-SHA256，自描述格式 <c>pbkdf2$迭代$盐hex$哈希hex</c>——单列存储免拆 schema）。</summary>
    private string HashPassword(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var iterations = Math.Max(1000, _options.PasswordIterations);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${iterations}${Convert.ToHexString(salt)}${Convert.ToHexString(hash)}";
    }

    private bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2" || !int.TryParse(parts[1], out var iterations))
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromHexString(parts[2]);
            expected = Convert.FromHexString(parts[3]);
        }
        catch (FormatException) { return false; }
        var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>账户列表（管理面——资金/持仓/活单汇总 + 是否已设密码）。</summary>
    public IReadOnlyList<SimAccountSummary> QueryAccounts()
    {
        lock (_gate)
        {
            return _states.Values
                .OrderBy(s => s.AccountId, StringComparer.Ordinal)
                .Select(s => new SimAccountSummary(
                    s.AccountId,
                    s.Account.BrokerId,
                    s.Account.PasswordHash is not null,
                    s.Account.Balance,
                    s.Account.Available,
                    s.Positions.Books.Sum(b => b.Long.Total),
                    s.Positions.Books.Sum(b => b.Short.Total),
                    s.Orders.All.Count(SimOrderManager.CanCancel)))
                .ToList();
        }
    }

    /// <summary>登出：仿真无连接可断，仅落盘（账户/持仓保留，下次登录续用；行情会话统计不归零）。</summary>
    public void Logout()
    {
        Flush();
    }

    /// <summary>
    /// 投资者 → 仿真账户键。空 <paramref name="userId"/> 归一到 <c>"sim"</c>。
    /// <para>
    /// 公开给服务端连接层（<c>SimConnection</c> 的登录绑定/查询归一）：真柜台的 <c>QueryAccountAsync</c> 传的是 <c>_brokerId</c>/<c>_userId</c>
    /// 而非登录入参，适配层必须用同一条归一规则算出同一个 key，否则查出来是空账户。
    /// </para>
    /// </summary>
    public static string AccountIdOf(string? userId) => string.IsNullOrWhiteSpace(userId) ? "sim" : userId;

    // —— 查询 ——

    /// <summary>查询资金。未登录/账号不存在 ⇒ 返回空账户（与真柜台零行时的行为一致）。</summary>
    public CtpAccount QueryAccount(string accountId)
    {
        lock (_gate)
        {
            var state = Find(accountId);
            if (state is null) return new CtpAccount { AccountId = accountId };
            return state.Account.ToDto();
        }
    }

    /// <summary>查询持仓（可选按合约过滤）。</summary>
    public IReadOnlyList<CtpPosition> QueryPositions(string accountId, string? instrumentId = null)
    {
        lock (_gate)
        {
            var state = Find(accountId);
            return state is null ? Array.Empty<CtpPosition>() : state.Positions.ToDto(instrumentId);
        }
    }

    /// <summary>查询报单（可选按合约过滤）。</summary>
    public IReadOnlyList<CtpOrder> QueryOrders(string accountId, string? instrumentId = null)
    {
        lock (_gate)
        {
            var state = Find(accountId);
            if (state is null) return Array.Empty<CtpOrder>();
            var all = state.Orders.All.Select(e => e.Order);
            if (!string.IsNullOrWhiteSpace(instrumentId))
                all = all.Where(o => string.Equals(o.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase));
            return all.ToList();
        }
    }

    /// <summary>查询合约元数据（仿真柜台独有的便利方法，非 <c>ICtpSession</c> 契约）——<b>全量表</b>（含下单/订阅派生出的合约）。</summary>
    public IReadOnlyList<SimInstrument> QueryInstruments()
    {
        lock (_gate) return _instruments.Values.OrderBy(i => i.InstrumentId, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 查询上市合约表（host 上架清单：<see cref="SimOptions.ListedInstruments"/> 或默认 zx001——「我就是交易所」的自定义面）。
    /// wire <c>queryInstruments</c> 的数据源；与全量表 <see cref="QueryInstruments"/> 区分。
    /// </summary>
    public IReadOnlyList<SimInstrument> QueryListedInstruments()
    {
        lock (_gate) return _listedBoard.ToList();
    }

    // —— 下单 / 撤单 ——

    /// <summary>
    /// 下单。**风控拒单抛 <see cref="CtpBusinessException"/>**——真柜台 <c>OrderInsertAsync</c> 的成功路径
    /// 不返回错误码（只回显 <c>OrderRef</c>/<c>RequestId</c>），异常是唯一能在 <c>ICtpSession</c> 接缝上表达拒单的通道。
    /// 市价单对手侧簿空 ⇒ <see cref="SimErrorCodes.NoCounterparty"/> 拒单（无庄家兜底——流动性由挂单供给）。
    /// </summary>
    public CtpOrderInsertResult InsertOrder(string accountId, CtpOrderRequest request)
    {
        // 多账户撮合：吃簿会成交**别的账户**的挂单（被动方也产 update）——按账户收集，分组持久化 + 分组抛事件
        var perAccount = new List<(string AccountId, SimOrderUpdate Update)>();
        CtpOrderInsertResult result;

        lock (_gate)
        {
            result = InsertOrderLocked(accountId, request, perAccount);
        }

        foreach (var group in perAccount.GroupBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase))
            Raise(group.Key, group.Select(x => x.Update).ToList());
        return result;
    }

    /// <summary>
    /// 下单主体（调用须持 <see cref="_gate"/>——所有账户同权同责走完整风控/清算/簿）。
    /// </summary>
    private CtpOrderInsertResult InsertOrderLocked(string accountId, CtpOrderRequest request,
        List<(string AccountId, SimOrderUpdate Update)> perAccount)
    {
        var state = GetOrCreate(accountId);
        var instrument = ResolveInstrument(request);

        // 市价单对手侧无他人活单 ⇒ 直接拒——没挂单就是没对手价（真实交易所语义）
        if (request.PriceType == CtpOrderPriceType.Market
            && !HasOppositeLiquidity(accountId, instrument.InstrumentId, request.Direction))
        {
            throw new CtpBusinessException(SimErrorCodes.NoCounterparty,
                $"无对手价：{instrument.InstrumentId} 订单簿{(request.Direction == CtpDirection.Buy ? "卖" : "买")}侧无挂单（无庄家兜底——流动性由挂单供给）。");
        }

        // post-only（本地协议扩展 TC '4'，BTC 对标 2026-10-05）：只入簿不吃单——插入时会交叉 ⇒ 整单拒收
        // （crypto GTX 语义：保证该单永远付 maker 费率；拒在报单号分配/冻结之前，无残留状态）。
        if (request.TimeCondition == CtpTimeCondition.PostOnly
            && request.PriceType != CtpOrderPriceType.Limit)
        {
            throw new CtpBusinessException(SimErrorCodes.InvalidParameter, "post-only 仅支持限价单。");
        }
        if (request.TimeCondition == CtpTimeCondition.PostOnly && WouldCrossBook(state, request, instrument))
        {
            throw new CtpBusinessException(SimErrorCodes.PostOnlyWouldCross,
                $"post-only 会吃单：{instrument.InstrumentId} 对手簿在限价 {(request.Direction == CtpDirection.Buy ? "上方" : "下方")}有他人挂单，整单拒收（保证只付 maker 费率）。");
        }

        var quote = Market.GetQuote(instrument.InstrumentId);
        var isClose = SimRiskEngine.IsClose(request.OffsetFlag);

        var refPrice = request.PriceType == CtpOrderPriceType.Market
            ? quote?.OppositeOf(request.Direction) ?? instrument.BasePrice
            : request.LimitPrice;

        // 平仓单不冻结保证金（平仓是减仓，不新增占用）；只冻手续费。
        // 冻结按 taker 费率全额（最坏情形保守冻结——BTC 对标 2026-10-05：maker 成交实际计费更少，
        // 差额留存冻结池、终态/撤销释放；未配置差异化费率的品种 taker=单一费率，行为与旧版一致）。
        var requiredMargin = isClose ? 0 : SimAccount.MarginOf(instrument, refPrice, request.Volume);
        var requiredFee = SimAccount.FeeOf(instrument, refPrice, request.Volume);

        var risk = _risk.CheckOrder(new RiskContext
        {
            Request = request,
            Instrument = instrument,
            RequiredMargin = requiredMargin,
            RequiredFee = requiredFee,
            Available = state.Account.Available,
            ClosableVolume = isClose
                ? state.Positions
                    .AvailableToClose(request.InstrumentId, PosiDirectionFor(request), request.OffsetFlag,
                        ReservedCloseVolume(state, request))
                    .Volume
                : null,
            InTradingTime = CtpTradingCalendar.IsTradingTime(_clock.Now),
        });
        if (!risk.Ok) throw new CtpBusinessException(risk.ErrorId, risk.Message);

        // M2 前置路径：直通客户端 OrderRef（⑲ 里客户端自产号——回报匹配/撤单定位都靠它）。
        // 用过外部号后推进序列：NextOrderRef 是前置自增（++seq 再用），置 max(seq, ref) ⇒ 下一号 = ref+1，
        // 不撞不跳（与重启恢复的推进同式）。重复号拒绝（同账户多连接并行下单的防御——已知限制）。
        if (!string.IsNullOrEmpty(request.OrderRef))
        {
            if (state.Orders.Find(request.OrderRef) is not null)
                throw new CtpBusinessException(SimErrorCodes.InvalidParameter,
                    $"报单号重复：{request.OrderRef}（同账户多连接并行下单会撞号，见已知限制）");
            state.NextOrderRefSeq = Math.Max(state.NextOrderRefSeq, ParseOrderRefSeq(request.OrderRef));
        }
        var orderRef = string.IsNullOrEmpty(request.OrderRef) ? NextOrderRef(state) : request.OrderRef;
        var u = state.Orders.Accept(request, orderRef, _frontId, _sessionId, instrument.ExchangeId,
            _clock.TimeOfDay, requiredMargin, requiredFee);
        state.Account.FreezeForOrder(requiredMargin, requiredFee);
        perAccount.Add((accountId, u));
        var result = new CtpOrderInsertResult(orderRef, RequestIdOf(orderRef));

        // 新单攻击方撮合：吃对手簿（价格-时间优先、maker 价、自成交跳过）→ 残量入簿/作废
        var entry = state.Orders.Find(orderRef)!;
        perAccount.AddRange(Aggress(state, entry, instrument));

        RecomputePositionProfitLocked();

        foreach (var (acct, update) in perAccount)
        {
            var s = Find(acct);
            if (s is not null) PersistOrder(s, update);
        }

        return result;
    }

    /// <summary>对手侧是否有<b>他人</b>活单（市价单闸门；自己的单不算对手——自成交跳过）。调用须持锁。</summary>
    private bool HasOppositeLiquidity(string accountId, string instrumentId, CtpDirection direction)
    {
        if (!_books.TryGetValue(instrumentId, out var book) || book.Count == 0) return false;
        var opposite = direction == CtpDirection.Buy ? book.AskSide() : book.BidSide();
        foreach (var be in opposite)
        {
            if (string.Equals(be.AccountId, accountId, StringComparison.OrdinalIgnoreCase)) continue;
            var state = Find(be.AccountId);
            var entry = state?.Orders.Find(be.OrderRef);
            if (entry is not null && SimOrderManager.CanCancel(entry) && entry.Remaining > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// post-only 闸门用：该限价委托插入时<b>是否会立即交叉</b>（对手簿在可吃边界内有他人活单——跳本账户，
    /// 与 <see cref="WalkBook"/> 一致的边界谓词与自成交跳过）。调用须持锁。风控拒单早于报单号分配——无残留状态。
    /// </summary>
    private bool WouldCrossBook(SimAccountState state, CtpOrderRequest request, SimInstrument instrument)
    {
        if (!_books.TryGetValue(instrument.InstrumentId, out var book) || book.Count == 0) return false;

        var probe = new CtpOrder { Direction = request.Direction, PriceType = request.PriceType, LimitPrice = request.LimitPrice };
        var (_, within) = BookCrossBound(probe);
        var opposite = request.Direction == CtpDirection.Buy ? book.AskSide() : book.BidSide();
        foreach (var be in opposite)
        {
            if (string.Equals(be.AccountId, state.AccountId, StringComparison.OrdinalIgnoreCase)) continue;   // 自成交跳过
            if (!within(be.Price)) break;   // 档位有序 ⇒ 首个出界即止
            var passiveState = Find(be.AccountId);
            var passiveEntry = passiveState?.Orders.Find(be.OrderRef);
            if (passiveEntry is not null && SimOrderManager.CanCancel(passiveEntry) && passiveEntry.Remaining > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 撤单。定位不到报单 ⇒ <see cref="SimErrorCodes.OrderNotFound"/>；
    /// 已终态 ⇒ <see cref="SimErrorCodes.OrderNotCancelable"/>（两者都抛 <see cref="CtpBusinessException"/>）。
    /// </summary>
    public void CancelOrder(string accountId, CtpOrderRef orderRef)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();

        lock (_gate)
        {
            var state = Find(accountId) ?? throw new CtpBusinessException(SimErrorCodes.NotLoggedIn, "未登录，无法撤单。");
            var entry = state.Orders.Find(orderRef.OrderRef)
                ?? throw new CtpBusinessException(SimErrorCodes.OrderNotFound,
                    $"撤单失败：报单 {orderRef.OrderRef} 不存在。");
            if (!SimOrderManager.CanCancel(entry))
                throw new CtpBusinessException(SimErrorCodes.OrderNotCancelable,
                    $"撤单失败：报单 {orderRef.OrderRef} 当前状态「{SimOrderStatus.Describe(entry.Order.OrderStatusRaw)}」不可撤。");

            CancelLocked(state, entry, updates);
        }

        Raise(accountId, updates.Select(x => x.Update).ToList());
    }

    /// <summary>撤单主体（调用须持锁）。出簿 + 交易所模式下推簿顶快照。</summary>
    private void CancelLocked(SimAccountState state, SimOrderEntry entry,
        List<(string AccountId, SimOrderUpdate Update)> perAccount)
    {
        var u = state.Orders.Cancel(entry, _clock.TimeOfDay);
        if (u is null) return;
        state.Account.ReleaseForOrder(u.ReleasedFrozenMargin, u.ReleasedFrozenFee);
        BookOf(entry.Order.InstrumentId).Remove(state.AccountId, entry.Order.OrderRef);   // 挂单出簿
        Market.OnBookChanged(entry.Order.InstrumentId);   // 撤单 ⇒ 簿顶变 ⇒ 推快照 tick
        perAccount.Add((state.AccountId, u));
        RecomputePositionProfitLocked();
        PersistOrder(state, u);
    }

    // —— 行情激活（订阅入口） ——

    /// <summary>
    /// 激活合约的行情（订阅时调用）。
    /// <para><b>上市闸门</b>——未上市 ⇒ false（不激活、无行情；上市表 = 唯一可订阅集合）。</para>
    /// <para>已上市：订阅即推当前快照 tick（此后 tick 全事件驱动——挂单/撤单/成交才推；空闲市场无后续事件）。
    /// 按下单同款解析链（精确 → 品种派生 → 合成兜底）补齐元数据（上市表解析在 <see cref="InitializeAsync"/> 已做过，这里为防御性重复）。</para>
    /// </summary>
    /// <returns>激活成功与否（未上市 ⇒ false，行情不推）。</returns>
    public bool ActivateMarket(string instrumentId)
    {
        lock (_gate)
        {
            if (!_listedIds.Contains(instrumentId ?? "")) return false;   // 未上市：不激活
            ResolveMarketInstrument(instrumentId ?? "");
            var ok = Market.Activate(instrumentId ?? "");
            if (ok)
                Market.OnBookChanged(instrumentId);   // 订阅即推快照（此后 tick 全事件驱动）
            return ok;
        }
    }

    /// <summary>
    /// 行情激活用的合约解析（品种派生/合成兜底，无订单上下文——合成锚价 0 ⇒ 基价 100）。
    /// 派生/合成结果入表并持久化（重启后精确命中，幂等）。
    /// </summary>
    private void ResolveMarketInstrument(string instrumentId)
    {
        var id = instrumentId ?? "";
        if (_instruments.TryGetValue(id, out _)) return;

        var derived = InstrumentCatalog.DeriveByVariety(id, _instruments);
        if (derived is not null)
        {
            _instruments[id] = derived;
            Persist(() => _store.SaveInstrumentAsync(derived));
            return;
        }

        var synthesized = InstrumentCatalog.Synthesize(id, 0);
        _instruments[id] = synthesized;
        Persist(() => _store.SaveInstrumentAsync(synthesized));
    }

    // —— 内部：撮合 ——

    /// <summary>取（或惰性建）某合约的订单簿。调用方须持锁。</summary>
    private OrderBook BookOf(string instrumentId)
    {
        if (!_books.TryGetValue(instrumentId, out var book))
        {
            book = new OrderBook();
            _books[instrumentId] = book;
        }
        return book;
    }

    /// <summary>
    /// 新单（攻击方）撮合：①吃对手簿（价格-时间优先、<b>maker 价</b>成交、自成交跳过——被动方也落账/落事件）；
    /// ②残量去向：GFD 限价入簿 / 市价·IOC·FOK 残量作废（真实交易所语义——无庄家兜底）。调用方须持锁。
    /// <para>簿成交<b>全深度</b>（簿对手是真单，不套流动性模拟上限）。</para>
    /// </summary>
    private List<(string AccountId, SimOrderUpdate Update)> Aggress(
        SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        var original = entry.Remaining;

        // FOK 干跑（先于任何落账——免回滚）：簿可吃量（跳本账户）< 原量 ⇒ 整单作废（无庄家兜底可补量）。
        if (entry.TimeCondition == CtpTimeCondition.FOK && BookFillable(state, entry, instrument) < original)
        {
            var dead = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay, "FOK 未能全部成交，已作废");
            if (dead is not null)
            {
                state.Account.ReleaseForOrder(dead.ReleasedFrozenMargin, dead.ReleasedFrozenFee);
                updates.Add((state.AccountId, dead));
            }
            return updates;
        }

        // ① 吃簿
        updates.AddRange(WalkBook(state, entry, instrument));

        // ② 残量去向（PostOnly = GFD + 插入禁交叉 ⇒ 残量照常入簿）
        if (entry.Remaining > 0 && entry.TimeCondition is not (CtpTimeCondition.GFD or CtpTimeCondition.PostOnly))
        {
            var tail = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay,
                entry.TimeCondition == CtpTimeCondition.FOK ? "FOK 未能全部成交，已作废" : "IOC 未成交部分已作废");
            if (tail is not null)
            {
                state.Account.ReleaseForOrder(tail.ReleasedFrozenMargin, tail.ReleasedFrozenFee);
                updates.Add((state.AccountId, tail));
            }
        }
        else if (entry.Remaining > 0 && entry.Order.PriceType == CtpOrderPriceType.Market)
        {
            // 市价残量不挂单（无逐 tick 重试的对手）——按「未成交不在队列」作废（真实交易所语义）
            var tail = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay, "市价单未成交部分作废（市价单不挂单）");
            if (tail is not null)
            {
                state.Account.ReleaseForOrder(tail.ReleasedFrozenMargin, tail.ReleasedFrozenFee);
                updates.Add((state.AccountId, tail));
            }
        }
        else if (entry.Remaining > 0 && entry.Order.PriceType == CtpOrderPriceType.Limit)
        {
            // GFD 限价残量入簿（等待对手方来吃）
            BookOf(instrument.InstrumentId).Add(state.AccountId, entry.Order.OrderRef,
                entry.Order.Direction, entry.Order.LimitPrice);
            Market.OnBookChanged(instrument.InstrumentId);   // 新挂单 ⇒ 簿顶变 ⇒ 推快照 tick
        }

        return updates;
    }

    /// <summary>
    /// 攻击方吃对手簿：方向 = 买吃卖侧 / 卖吃买侧；<b>跳过本账户</b>（自成交防护）；
    /// <b>可吃边界 = 限价本身</b>（市价单无自有限价 ⇒ 无界吃全簿）；
    /// 档位有序 ⇒ 首个出界即止；每档成交量 = 双方余量取小、<b>成交价 = 挂单价（maker 价）</b>。调用方须持锁。
    /// </summary>
    private List<(string AccountId, SimOrderUpdate Update)> WalkBook(
        SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        var book = BookOf(instrument.InstrumentId);
        if (book.Count == 0 || entry.Remaining <= 0) return updates;   // 空簿 fast path

        // 簿即全部市场——可吃边界 = 限价本身（市价无界吃全簿）
        var (bound, within) = BookCrossBound(entry.Order);
        var opposite = entry.Order.Direction == CtpDirection.Buy ? book.AskSide() : book.BidSide();
        foreach (var be in opposite)
        {
            if (entry.Remaining <= 0) break;
            if (string.Equals(be.AccountId, state.AccountId, StringComparison.OrdinalIgnoreCase)) continue;   // 自成交跳过
            if (!within(be.Price)) break;   // 档位有序 ⇒ 首个出界即止

            var passiveState = Find(be.AccountId);
            if (passiveState is null) continue;
            var passiveEntry = passiveState.Orders.Find(be.OrderRef);
            if (passiveEntry is null || !SimOrderManager.CanCancel(passiveEntry)) continue;   // 陈旧簿条目防御

            var volume = Math.Min(entry.Remaining, passiveEntry.Remaining);
            if (volume <= 0) continue;
            var price = instrument.RoundToTick(be.Price);

            // 双方各自 ApplyFill（TradeId 从各自账户序列铸）+ 各自记账；被动方先落（maker 语义上先存在）。
            // 手续费按成交角色计（BTC 对标 2026-10-05）：被动方 maker 费率、攻击方 taker 费率（按成交价 × 实际量；
            // 攻击单残量入簿后被后来者吃时自动站到 passive 侧 ⇒ 付 maker 费——角色天然自洽）。
            var up = passiveState.Orders.ApplyFill(passiveEntry, volume, price, _clock.TimeOfDay,
                SimAccount.FeeOf(instrument, price, volume, isMaker: true));
            ApplyFillAccounting(passiveState, instrument, up);
            updates.Add((passiveState.AccountId, up));
            if (!SimOrderManager.CanCancel(passiveEntry)) book.Remove(be.AccountId, be.OrderRef);

            var ua = state.Orders.ApplyFill(entry, volume, price, _clock.TimeOfDay,
                SimAccount.FeeOf(instrument, price, volume, isMaker: false));
            ApplyFillAccounting(state, instrument, ua);
            updates.Add((state.AccountId, ua));

            // 成交即行情——最新价/累计量更新 + 立即外发含新簿顶的 tick；成交 tick 落盘（真实交易消息）
            var tick = Market.RecordTrade(instrument.InstrumentId, price, volume);
            if (tick is not null) Persist(() => _store.SaveTickAsync(tick));
        }

        return updates;
    }

    /// <summary>FOK 干跑用：对手簿可吃量（<b>跳本账户</b>——与 <see cref="WalkBook"/> 一致的可吃边界与自成交跳过）。调用方须持锁。</summary>
    private int BookFillable(SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var book = BookOf(instrument.InstrumentId);
        if (book.Count == 0) return 0;

        var (bound, within) = BookCrossBound(entry.Order);
        var total = 0;
        var opposite = entry.Order.Direction == CtpDirection.Buy ? book.AskSide() : book.BidSide();
        foreach (var be in opposite)
        {
            if (string.Equals(be.AccountId, state.AccountId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!within(be.Price)) break;
            var passiveState = Find(be.AccountId);
            if (passiveState is null) continue;
            var passiveEntry = passiveState.Orders.Find(be.OrderRef);
            if (passiveEntry is null || !SimOrderManager.CanCancel(passiveEntry)) continue;
            total += passiveEntry.Remaining;
        }
        return total;
    }

    /// <summary>
    /// 簿可吃边界：买 ⇒ 档位价 ≤ 限价；卖 ⇒ 档位价 ≥ 限价。市价单无自有限价 ⇒ 无界（吃全簿）。
    /// 返回谓词（档位有序 ⇒ break 友好）。
    /// </summary>
    private static (double Bound, Func<double, bool> Within) BookCrossBound(CtpOrder order)
    {
        var isBuy = order.Direction == CtpDirection.Buy;
        var limit = order.PriceType == CtpOrderPriceType.Limit ? order.LimitPrice : double.NaN;

        double bound;
        if (isBuy)
        {
            bound = double.IsNaN(limit) ? double.MaxValue : limit;
            return (bound, p => p <= bound + 1e-9);
        }
        bound = double.IsNaN(limit) ? double.MinValue : limit;
        return (bound, p => p >= bound - 1e-9);
    }

    // —— 行情源回调（引擎经 BindBook 持有；全部在 _gate 内调用）——

    /// <summary>簿顶快照：同价档聚合量、陈旧条目跳过、空侧 (0, 0)。调用须持 <see cref="_gate"/>。</summary>
    private BookTop? BookTopSnapshot(string instrumentId)
    {
        if (!_books.TryGetValue(instrumentId, out var book) || book.Count == 0) return null;
        var bid = TopLevel(book.BidSide());
        var ask = TopLevel(book.AskSide());
        return new BookTop(bid.Price, bid.Volume, ask.Price, ask.Volume);
    }

    /// <summary>某侧簿顶（快照已按价格优先排序）：首个活单的价 + 同价档聚合量。</summary>
    private (double Price, int Volume) TopLevel(IReadOnlyList<BookEntry> side)
    {
        double? topPrice = null;
        var volume = 0;
        foreach (var be in side)
        {
            var state = Find(be.AccountId);
            var entry = state?.Orders.Find(be.OrderRef);
            if (entry is null || !SimOrderManager.CanCancel(entry) || entry.Remaining <= 0) continue;   // 陈旧条目防御
            if (topPrice is null) topPrice = be.Price;
            if (Math.Abs(be.Price - topPrice.Value) > 1e-9) break;   // 顶档同价聚合完毕
            volume += entry.Remaining;
        }
        return (topPrice ?? 0, volume);
    }

    /// <summary>某合约全市场持仓量（单边计：多头合计——空头对称）。交易所模式 tick 的 <c>OpenInterest</c>。</summary>
    private double OpenInterestOf(string instrumentId)
    {
        var oi = 0;
        foreach (var state in _states.Values)
        {
            var book = state.Positions.Find(instrumentId);
            if (book is not null) oi += book.Long.Total;
        }
        return oi;
    }

    /// <summary>成交记账：开仓把冻结转占用、平仓按比例释放占用并结转盈亏。调用方须持锁。</summary>
    private static void ApplyFillAccounting(SimAccountState state, SimInstrument instrument, SimOrderUpdate u)
    {
        var trade = u.Trade!;
        var account = state.Account;

        if (SimRiskEngine.IsClose(trade.OffsetFlag))
        {
            var posiDirection = trade.Direction == CtpDirection.Buy ? CtpPosiDirection.Short : CtpPosiDirection.Long;
            var close = state.Positions.ApplyCloseFill(trade.InstrumentId, posiDirection, trade.OffsetFlag,
                trade.Volume, trade.Price, instrument);
            account.ReleasePositionMargin(close.ReleasedMargin);
            account.ApplyClosedPnl(close.Pnl);
        }
        else
        {
            var posiDirection = trade.Direction == CtpDirection.Buy ? CtpPosiDirection.Long : CtpPosiDirection.Short;
            state.Positions.ApplyOpenFill(trade.InstrumentId, instrument.ExchangeId, posiDirection,
                trade.Volume, trade.Price, instrument);
            account.ConsumeFrozenMargin(u.ConsumedFrozenMargin);
        }

        account.ConsumeFrozenFee(u.ConsumedFrozenFee);
        account.ReleaseForOrder(u.ReleasedFrozenMargin, u.ReleasedFrozenFee);
    }

    // —— 内部：事件 ——

    private void Raise(string accountId, List<SimOrderUpdate> updates)
    {
        if (updates.Count == 0) return;
        foreach (var u in updates)
        {
            OrderChanged?.Invoke(accountId, u.Order);
            if (u.Trade is not null) TradeFilled?.Invoke(accountId, u.Trade);
        }
    }

    // —— 内部：状态与工具 ——

    private SimAccountState GetOrCreate(string accountId)
    {
        if (_states.TryGetValue(accountId, out var state)) return state;
        state = new SimAccountState(new SimAccount(accountId, _options.InitialBalance));
        _states[accountId] = state;
        return state;
    }

    private SimAccountState? Find(string accountId)
        => _states.TryGetValue(accountId ?? "", out var s) ? s : null;

    /// <summary>
    /// 合约解析：<b>上市闸门</b>（未上市 ⇒ 拒 90002——上市表是唯一可交易集合，上架是显式决定）；
    /// 然后精确 → 按品种派生 → 合成兜底（防御性——上市 id 在 <see cref="InitializeAsync"/> 已解析入表，这里通常精确命中）。
    /// </summary>
    private SimInstrument ResolveInstrument(CtpOrderRequest request)
    {
        var id = request.InstrumentId ?? "";
        if (!_listedIds.Contains(id))
            throw new CtpBusinessException(SimErrorCodes.UnknownInstrument,
                $"未上市：{id} 不在上市合约表（上市清单见行情板；host 经 AETHER_CTP_SIM_BOARD 自定义上架）。");
        if (_instruments.TryGetValue(id, out var exact)) return exact;

        var derived = InstrumentCatalog.DeriveByVariety(id, _instruments);
        if (derived is not null)
        {
            _instruments[id] = derived;
            Persist(() => _store.SaveInstrumentAsync(derived));
            return derived;
        }

        var synthesized = InstrumentCatalog.Synthesize(id, request.LimitPrice);
        _instruments[id] = synthesized;
        Persist(() => _store.SaveInstrumentAsync(synthesized));
        return synthesized;
    }

    private SimInstrument? InstrumentOf(string instrumentId)
        => _instruments.TryGetValue(instrumentId ?? "", out var i) ? i : null;

    /// <summary>持仓方向：买开→多仓、卖开→空仓；平多→多仓、平空→空仓（由开平标志与方向共同决定）。</summary>
    private static CtpPosiDirection PosiDirectionFor(CtpOrderRequest request)
    {
        // 开仓：买→多、卖→空。平仓：平的是对手方向（买平 → 平空仓；卖平 → 平多仓）。
        // 仿真沿用 CTP 惯例：下单请求不带持仓方向，由交易所按方向 + 开平推得，
        // 这里按「买开/卖平 → 多仓；卖开/买平 → 空仓」推（与期货页 UI 的语义一致）。
        return SimRiskEngine.IsClose(request.OffsetFlag)
            ? request.Direction == CtpDirection.Buy ? CtpPosiDirection.Short : CtpPosiDirection.Long
            : request.Direction == CtpDirection.Buy ? CtpPosiDirection.Long : CtpPosiDirection.Short;
    }

    /// <summary>该合约该方向已被挂单中的平仓单占用的手数（防同一批持仓被重复平仓）。</summary>
    private static int ReservedCloseVolume(SimAccountState state, CtpOrderRequest request)
    {
        // 平仓单平的是「对手方向」的持仓，故占用只看**同向**的平仓挂单
        // （买平 → 平空仓；另一张买平单占用的是同一批空仓）。
        var reserved = 0;
        foreach (var entry in state.Orders.All)
        {
            if (!SimOrderManager.CanCancel(entry)) continue;
            if (!SimRiskEngine.IsClose(entry.Order.OffsetFlag)) continue;
            if (!string.Equals(entry.Order.InstrumentId, request.InstrumentId, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Order.Direction != request.Direction) continue;
            reserved += entry.Remaining;
        }
        return reserved;
    }

    private string NextOrderRef(SimAccountState state)
    {
        state.NextOrderRefSeq++;
        return state.NextOrderRefSeq.ToString("D12", CultureInfo.InvariantCulture);
    }

    /// <summary>报单号 → 序号（非本柜台格式的号一律算 0，不推进序列）。</summary>
    private static int ParseOrderRefSeq(string orderRef)
        => int.TryParse(orderRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;

    /// <summary>已落盘成交号的最大值（号非整数时算 0——只影响自身，不影响别的号）。</summary>
    private static long MaxTradeId(IReadOnlyList<string> tradeIds)
    {
        var max = 0L;
        foreach (var id in tradeIds)
            if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > max) max = v;
        return max;
    }

    /// <summary>报单号 → 请求号（真柜台是独立递增序列；仿真用同一个数即可，仅作回显）。</summary>
    private static int RequestIdOf(string orderRef)
        => int.TryParse(orderRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void RecomputePositionProfit()
    {
        lock (_gate) RecomputePositionProfitLocked();
    }

    private void RecomputePositionProfitLocked()
    {
        foreach (var state in _states.Values)
        {
            var profit = state.Positions.RecomputePositionProfit(
                LastPriceOf, InstrumentOf);
            state.Account.SetPositionProfit(profit);
        }
    }

    private double LastPriceOf(string instrumentId)
    {
        var q = Market.GetQuote(instrumentId);
        return q?.LastPrice ?? 0;
    }

    // —— 内部：落盘（尽力而为） ——

    private void PersistOrder(SimAccountState state, SimOrderUpdate update)
    {
        Persist(() => _store.SaveOrderAsync(state.AccountId, update.Order));
        if (update.Trade is not null)
        {
            var trade = update.Trade;
            Persist(() => _store.SaveTradeAsync(state.AccountId, trade));
        }
        Persist(() => _store.SaveAccountAsync(state.Account.ToSnapshot()));
        Persist(() => _store.SavePositionsAsync(state.AccountId, state.Positions.ToSnapshots().ToList()));
    }

    /// <summary>
    /// 登记一次 fire-and-forget 落盘。**调用方须持锁**（访问 <see cref="_pendingWrites"/>）。
    /// 异常只 <c>Trace</c>——仿真柜台的磁盘故障不得影响交易链路。
    /// </summary>
    private void Persist(Func<Task> write)
    {
        _pendingWrites.RemoveAll(t => t.IsCompleted);
        try
        {
            var task = Task.Run(async () =>
            {
                try { await write().ConfigureAwait(false); }
                catch (Exception ex) { Trace.WriteLine($"[CTP-SIM] 落盘失败（已忽略）：{ex.Message}"); }
            });
            _pendingWrites.Add(task);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[CTP-SIM] 落盘调度失败（已忽略）：{ex.Message}");
        }
    }

    /// <summary>等待挂起写盘（登出等需要立即一致的场合）。</summary>
    private void Flush()
    {
        Task[] pending;
        lock (_gate) pending = _pendingWrites.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length == 0) return;
        try { Task.WhenAll(pending).GetAwaiter().GetResult(); }
        catch (Exception ex) { Trace.WriteLine($"[CTP-SIM] 落盘刷新异常：{ex.Message}"); }
    }
}
