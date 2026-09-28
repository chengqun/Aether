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
/// 职责：账户/持仓/报单状态 + 风控 + 撮合 + 随机游走行情 + 落盘。
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
    private readonly IRiskEngine _risk;
    private readonly IMatchingEngine _matching;
    private readonly ISimStore _store;
    private readonly object _gate = new();

    private readonly Dictionary<string, SimAccountState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SimInstrument> _instruments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _pendingWrites = new();

    /// <summary>合约订单簿（混合撮合的客户端互为对手层）。全 mutation 在 <see cref="_gate"/> 内。</summary>
    private readonly Dictionary<string, OrderBook> _books = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上市合约表（host 上架清单——wire <c>queryInstruments</c> 的数据源；与全量表 <see cref="QueryInstruments"/> 区分）。</summary>
    private readonly List<SimInstrument> _listedBoard = new();

    /// <summary>上市合约 id 集（交易所模式的可交易/可订阅闸门——上市表 = 唯一入口）。</summary>
    private readonly HashSet<string> _listedIds = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _feedTimer;
    private int _frontId = 1;
    private int _sessionId;

    /// <summary>交易所模式（价格发现归簿：行情 = 簿顶 + 成交流，庄家兜底关闭）。</summary>
    private readonly bool _exchange;

    /// <summary>做市商义务监测器（统计面；报价自律在策略侧——market-maker.lean.cs）。</summary>
    private readonly MmObligationMonitor _mmMonitor = new();

    public SimTradingCore(
        SimOptions options,
        SimClock? clock = null,
        IRiskEngine? risk = null,
        IMatchingEngine? matching = null,
        ISimStore? store = null)
    {
        _options = options;
        _clock = clock ?? new SimClock();
        _risk = risk ?? new SimRiskEngine(options);
        _matching = matching ?? new SimpleMatchingEngine(options);
        _store = store ?? StoreOf(options);
        _exchange = options.MarketMode == SimMarketMode.Exchange;

        Market = new SimMarketDataEngine(options, _clock, InstrumentOf);
        Market.TickGenerated += (_, tick) => TickProduced?.Invoke(tick);
        if (_exchange)
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

    /// <summary>随机游走行情引擎（Phase 2 若提供 Md 仿真亦复用同一个）。</summary>
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

            // 上市合约表（host 上架清单）：null/空 ⇒ 默认 = sim001（本交易所自有品种——「我就是交易所」；
            // 2026-09-27 起从种子 9 合约改为单合约，价格源 = 随机游走/回放/（未来）真实转发）。
            // 自定义 id 经解析链（精确→品种派生→合成）落表；种子 9 合约仍可用 AETHER_CTP_SIM_BOARD 上架。
            var listedIds = _options.ListedInstruments is { Count: > 0 }
                ? _options.ListedInstruments
                : new[] { "sim001" };
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
        // 不归零回昨结算基准（成交是真实交易消息，跨重启延续；walk 模式无 tick ⇒ 天然无操作）。
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

    /// <summary>启动随机游走（<see cref="SimOptions.TickIntervalMs"/> 为 0 时不启动，由调用方手动推进）。交易所模式无游走——恒 no-op。</summary>
    public void StartMarketFeed()
    {
        if (_exchange || _options.TickIntervalMs <= 0) return;
        lock (_gate)
        {
            _feedTimer ??= new Timer(_ => AdvanceMarket(), null, _options.TickIntervalMs, _options.TickIntervalMs);
        }
    }

    /// <summary>停止随机游走。</summary>
    public void StopMarketFeed()
    {
        lock (_gate)
        {
            _feedTimer?.Dispose();
            _feedTimer = null;
        }
    }

    /// <summary>释放（幂等）。</summary>
    public void Dispose()
    {
        StopReplay();
        StopMarketFeed();
        _mmMonitorTimer?.Dispose();
        _mmMonitorTimer = null;
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

    /// <summary>登出：仿真无连接可断，仅停止行情推进（账户/持仓保留，下次登录续用）。</summary>
    public void Logout()
    {
        StopMarketFeed();
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
    /// 查询上市合约表（host 上架清单：<see cref="SimOptions.ListedInstruments"/> 或默认 9 种子——「我就是交易所」的自定义面）。
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
    /// 交易所模式下市价单对手侧簿空 ⇒ <see cref="SimErrorCodes.NoCounterparty"/> 拒单（无庄家兜底）。
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

        // 交易所模式：市价单对手侧无他人活单 ⇒ 直接拒——没挂单就是没对手价（真实交易所语义）
        if (_exchange && request.PriceType == CtpOrderPriceType.Market
            && !HasOppositeLiquidity(accountId, instrument.InstrumentId, request.Direction))
        {
            throw new CtpBusinessException(SimErrorCodes.NoCounterparty,
                $"无对手价：{instrument.InstrumentId} 订单簿{(request.Direction == CtpDirection.Buy ? "卖" : "买")}侧无挂单（交易所模式无庄家兜底）。");
        }

        var quote = Market.GetQuote(instrument.InstrumentId);
        var isClose = SimRiskEngine.IsClose(request.OffsetFlag);

        var refPrice = request.PriceType == CtpOrderPriceType.Market
            ? quote?.OppositeOf(request.Direction) ?? instrument.BasePrice
            : request.LimitPrice;

        // 平仓单不冻结保证金（平仓是减仓，不新增占用）；只冻手续费
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

        var orderRef = NextOrderRef(state);
        var u = state.Orders.Accept(request, orderRef, _frontId, _sessionId, instrument.ExchangeId,
            _clock.TimeOfDay, requiredMargin, requiredFee);
        state.Account.FreezeForOrder(requiredMargin, requiredFee);
        perAccount.Add((accountId, u));
        var result = new CtpOrderInsertResult(orderRef, RequestIdOf(orderRef));

        // 新单攻击方撮合：吃对手簿（价格-时间优先、maker 价、自成交跳过）→ 余量庄家兜底（交易所模式无）→ 残量入簿/作废
        var entry = state.Orders.Find(orderRef)!;
        perAccount.AddRange(Aggress(state, entry, instrument));

        // 既有「同账户其它活单重匹配（庄家向）」——排除新单（其庄家匹配已在 Aggress 做过，引擎 rng 不双抽）。
        // 交易所模式无庄家——价格只由簿驱动，跳过。
        if (!_exchange)
        {
            foreach (var update in Match(state, instrument, excludeOrderRef: orderRef))
                perAccount.Add((accountId, update));
        }

        RecomputePositionProfitLocked();

        foreach (var (acct, update) in perAccount)
        {
            var s = Find(acct);
            if (s is not null) PersistOrder(s, update);
        }

        return result;
    }

    /// <summary>对手侧是否有<b>他人</b>活单（交易所模式市价单闸门；自己的单不算对手——自成交跳过）。调用须持锁。</summary>
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
        if (_exchange) Market.OnBookChanged(entry.Order.InstrumentId);   // 撤单 ⇒ 簿顶变 ⇒ 推快照 tick
        perAccount.Add((state.AccountId, u));
        RecomputePositionProfitLocked();
        PersistOrder(state, u);
    }

    // —— 行情推进（撮合挂单） ——

    /// <summary>
    /// 推进一个 tick：随机游走 + 用新价重试全部挂单。
    /// 供定时器调用，也供单测手动驱动（<see cref="SimOptions.TickIntervalMs"/> = 0 时不自动跑）。
    /// 交易所模式无游走、挂单由对手方插入驱动撮合——恒 no-op。
    /// </summary>
    public void AdvanceMarket()
    {
        if (_exchange) return;
        var perAccount = new List<(string AccountId, SimOrderUpdate Update)>();

        lock (_gate)
        {
            Market.Advance();

            // ① 簿内交叉扫：价格漂移使最优买 ≥ 最优卖（不同账户）⇒ 按 maker 价（先挂者价）互吃
            CrossMatchBooks(perAccount);

            // ② 既有庄家撮合循环（全账户活单 vs 新报价）
            foreach (var state in _states.Values)
            {
                foreach (var update in Match(state, instrument: null))
                    perAccount.Add((state.AccountId, update));
            }
            RecomputePositionProfitLocked();

            foreach (var (accountId, update) in perAccount)
            {
                var state = Find(accountId);
                if (state is not null) PersistOrder(state, update);
            }
        }

        // 按账号分组推事件（组内保持发生次序）
        foreach (var group in perAccount.GroupBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase))
            Raise(group.Key, group.Select(x => x.Update).ToList());
    }

    /// <summary>
    /// 激活合约的行情（订阅时调用）。
    /// <para><b>交易所模式：上市闸门</b>——未上市 ⇒ false（不激活、无行情、做市不进场；上市表 = 唯一可订阅集合）。</para>
    /// <para>walk 模式：<see cref="AdvanceMarket"/> 只推进已激活的合约——只订阅不激活则行情永远不来；
    /// 按下单同款解析链（精确 → 品种派生 → 合成兜底）补齐元数据（期货页行情板订阅 ~250 个主力合约，绝大多数不在种子表内）。</para>
    /// <para>交易所模式（已上市）：订阅即推当前快照 tick（此后 tick 全事件驱动——没有快照，订阅后要等下一个簿/成交事件才有价）；
    /// 并让做市机器人对合约挂双边报价（先报价再快照——首帧就有价）。</para>
    /// </summary>
    /// <returns>激活成功与否（未上市 / 严格模式下未知品种 ⇒ false，行情不推）。</returns>
    public bool ActivateMarket(string instrumentId)
    {
        var perAccount = new List<(string AccountId, SimOrderUpdate Update)>();
        bool ok;
        lock (_gate)
        {
            if (_exchange && !_listedIds.Contains(instrumentId ?? "")) return false;   // 未上市：不激活（perAccount 空——raise 空转）
            ResolveMarketInstrument(instrumentId);
            ok = Market.Activate(instrumentId);
            if (ok && _exchange)
                Market.OnBookChanged(instrumentId);   // 订阅即推快照（此后 tick 全事件驱动——做市报价由外部客户端策略供给）
        }
        foreach (var group in perAccount.GroupBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase))
            Raise(group.Key, group.Select(x => x.Update).ToList());
        return ok;
    }

    /// <summary>
    /// 行情激活用的合约解析（品种派生/合成兜底，无订单上下文——合成锚价 0 ⇒ 基价 100）。
    /// 派生/合成结果入表并持久化（重启后精确命中，幂等）。严格模式拒未知品种 ⇒ 返回，激活失败
    /// （行情不推）；拒单语义仍留给下单路径。
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

        if (!_options.AcceptUnknownInstruments) return;
        var synthesized = InstrumentCatalog.Synthesize(id, 0);
        _instruments[id] = synthesized;
        Persist(() => _store.SaveInstrumentAsync(synthesized));
    }

    /// <summary>手动注入行情（测试 + <b>历史 tick 回放</b>驱动）。注入后立即重试挂单。volume（累计量）可选——回放保真用。
    /// 注入前先经解析链补合约元数据（任意 id 可注——未登记合约静默 no-op 是坑）。
    /// <b>交易所模式 no-op</b>——价格源互斥：交易所模式的价只能来自簿与成交（host 层也不会在交易所模式起回放）。</summary>
    public void InjectQuote(string instrumentId, double bid, double ask, double last, long? volume = null)
    {
        if (_exchange) return;
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        lock (_gate)
        {
            ResolveMarketInstrument(instrumentId);
            Market.Inject(instrumentId, bid, ask, last, volume);
            foreach (var state in _states.Values)
                foreach (var update in Match(state, instrument: null))
                    updates.Add((state.AccountId, update));
            RecomputePositionProfitLocked();
        }
        foreach (var group in updates.GroupBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase))
            Raise(group.Key, group.Select(x => x.Update).ToList());
    }

    // —— 历史 tick 回放（「我就是交易所」的确定性测试价格源；交易所模式（默认）= 价格发现归簿；
    //     随机游走 = 显式退路。真实行情转发明确不做——本柜台是自己的交易所，不接外部期货行情） ——

    /// <summary>
    /// 启动历史 tick 回放驱动：按 CSV 时间轴（<c>time,instrument,bid,ask,last[,volume]</c>）驱动
    /// <see cref="InjectQuote"/>——注价即重试挂单，撮合/推送/行情板全复用。**自动停掉随机 feed**
    /// （游走不再污染回放价）。速度倍率 &gt;1 快放；loop = 到文件尾停 1s 循环（沙盒常有市）。
    /// 文件不存在/表头不符/无有效行 ⇒ <see cref="ArgumentException"/>（host 侧柜台退出——不静默换价格源）。
    /// </summary>
    public void StartReplay(string csvPath, double speed = 1, bool loop = true)
    {
        StopMarketFeed();   // 回放是唯一价格源——随机游走必须让位
        StopReplay();
        _replay = TickReplayDriver.Start(csvPath, t =>
            InjectQuote(t.InstrumentId, t.Bid, t.Ask, t.Last, t.Volume), speed, loop);
    }

    /// <summary>停止回放驱动（价格停在最后一帧——撮合仍对最后报价可用；随机 feed 不自动恢复）。</summary>
    public void StopReplay()
    {
        var driver = _replay;
        _replay = null;
        driver?.Dispose();
    }

    /// <summary>回放是否在跑。</summary>
    public bool IsReplaying => _replay is not null;

    private TickReplayDriver? _replay;

    // —— 内部：撮合 ——

    /// <summary>
    /// 对某账户的全部活单尝试撮合（<paramref name="instrument"/> 非空时只撮该合约）——下单后与每 tick 共用。
    /// 调用方须持锁。返回的状态变化按发生次序排列。庄家向（vs 随机游走报价）；吃簿在
    /// <see cref="Aggress"/>/<see cref="CrossMatchBooks"/>。
    /// </summary>
    private List<SimOrderUpdate> Match(SimAccountState state, SimInstrument? instrument, string? excludeOrderRef = null)
    {
        var updates = new List<SimOrderUpdate>();

        // 快照以避免撮合过程中集合被改动；顺序 = 下单先后（价格优先/时间优先的简化版）
        var live = state.Orders.All.Where(SimOrderManager.CanCancel).ToList();
        foreach (var entry in live)
        {
            if (excludeOrderRef is not null && entry.Order.OrderRef == excludeOrderRef) continue;

            if (instrument is not null
                && !string.Equals(entry.Order.InstrumentId, instrument.InstrumentId, StringComparison.OrdinalIgnoreCase))
                continue;

            var meta = InstrumentOf(entry.Order.InstrumentId);
            if (meta is null) continue;

            var quote = Market.GetQuote(entry.Order.InstrumentId);
            var outcome = _matching.Match(entry.Order, quote, meta);

            // FOK：不能全成则完全不成交（一笔都不记，直接作废）
            if (entry.TimeCondition == CtpTimeCondition.FOK
                && outcome.Filled && outcome.FillVolume < entry.Remaining)
            {
                outcome = MatchOutcome.None;
            }

            if (outcome.Filled)
            {
                var u = state.Orders.ApplyFill(entry, outcome.FillVolume, outcome.FillPrice, _clock.TimeOfDay);
                ApplyFillAccounting(state, meta, u);
                updates.Add(u);
                // 庄家成交使挂单离队 ⇒ 出簿（挂单的唯一离队路径之一；另一为撤单）
                if (!SimOrderManager.CanCancel(entry))
                    BookOf(entry.Order.InstrumentId).Remove(state.AccountId, entry.Order.OrderRef);
            }

            // 残量去向：GFD 继续挂单；IOC/FOK 残量作废（释放冻结）
            if (entry.Remaining > 0 && entry.TimeCondition != CtpTimeCondition.GFD)
            {
                var tail = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay,
                    entry.TimeCondition == CtpTimeCondition.FOK ? "FOK 未能全部成交，已作废" : "IOC 未成交部分已作废");
                if (tail is not null)
                {
                    state.Account.ReleaseForOrder(tail.ReleasedFrozenMargin, tail.ReleasedFrozenFee);
                    updates.Add(tail);
                }
            }
        }

        return updates;
    }

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
    /// ②余量庄家兜底（<see cref="SimpleMatchingEngine"/>——空簿时即今日路径，<b>逐字节等价</b>）；
    /// ③残量去向：GFD 限价入簿 / IOC·FOK 作废（既有语义）。调用方须持锁。
    /// <para>簿成交<b>全深度</b>——不套 MaxFillVolume/PartialFillChance（那是庄家流动性模拟，簿对手是真单）。</para>
    /// </summary>
    private List<(string AccountId, SimOrderUpdate Update)> Aggress(
        SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        var quote = Market.GetQuote(instrument.InstrumentId);
        var original = entry.Remaining;

        // FOK 干跑（先于任何落账——免回滚）：簿可吃量（跳本账户）+ 庄家一次 Match 合量 < 原量 ⇒ 整单作废。
        // 干跑与执行共用同一次引擎调用（PartialFillChance 的 rng 只抽一次）。交易所模式无庄家 ⇒ 只看簿量。
        MatchOutcome? housePlan = null;
        if (entry.TimeCondition == CtpTimeCondition.FOK)
        {
            var bookFillable = BookFillable(state, entry, instrument);
            var dry = _exchange ? MatchOutcome.None : _matching.Match(entry.Order, quote, instrument);
            if (bookFillable + dry.FillVolume < original)
            {
                var dead = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay, "FOK 未能全部成交，已作废");
                if (dead is not null)
                {
                    state.Account.ReleaseForOrder(dead.ReleasedFrozenMargin, dead.ReleasedFrozenFee);
                    updates.Add((state.AccountId, dead));
                }
                return updates;
            }
            housePlan = dry;
        }

        // ① 吃簿
        updates.AddRange(WalkBook(state, entry, instrument));

        // ② 庄家兜底（余量）：GFD/IOC 现调一次引擎（今日语义，rng 单抽）；FOK 复用干跑结果按余量夹取。
        // 交易所模式无庄家——余量按 ③ 残量去向处理（限价入簿 / 市价作废）。
        MatchOutcome outcome;
        if (_exchange) outcome = MatchOutcome.None;
        else if (housePlan is { } plan)
            outcome = plan.FillVolume > entry.Remaining ? plan with { FillVolume = entry.Remaining } : plan;
        else
            outcome = _matching.Match(entry.Order, quote, instrument);

        if (outcome.Filled)
        {
            var u = state.Orders.ApplyFill(entry, outcome.FillVolume, outcome.FillPrice, _clock.TimeOfDay);
            ApplyFillAccounting(state, instrument, u);
            updates.Add((state.AccountId, u));
        }

        // ③ 残量去向
        if (entry.Remaining > 0 && entry.TimeCondition != CtpTimeCondition.GFD)
        {
            var tail = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay,
                entry.TimeCondition == CtpTimeCondition.FOK ? "FOK 未能全部成交，已作废" : "IOC 未成交部分已作废");
            if (tail is not null)
            {
                state.Account.ReleaseForOrder(tail.ReleasedFrozenMargin, tail.ReleasedFrozenFee);
                updates.Add((state.AccountId, tail));
            }
        }
        else if (entry.Remaining > 0 && _exchange && entry.Order.PriceType == CtpOrderPriceType.Market)
        {
            // 交易所模式：市价残量不挂单（无庄家逐 tick 重试）——按「未成交不在队列」作废（真实交易所语义）
            var tail = state.Orders.FinalizeNoQueue(entry, _clock.TimeOfDay, "市价单未成交部分作废（交易所模式市价单不挂单）");
            if (tail is not null)
            {
                state.Account.ReleaseForOrder(tail.ReleasedFrozenMargin, tail.ReleasedFrozenFee);
                updates.Add((state.AccountId, tail));
            }
        }
        else if (entry.Remaining > 0 && entry.Order.PriceType == CtpOrderPriceType.Limit)
        {
            // GFD 限价残量入簿（walk 模式下市价 GFD 残量不入簿——沿用「市价单靠庄家每 tick 重试」的既有行为）
            BookOf(instrument.InstrumentId).Add(state.AccountId, entry.Order.OrderRef,
                entry.Order.Direction, entry.Order.LimitPrice);
            if (_exchange) Market.OnBookChanged(instrument.InstrumentId);   // 新挂单 ⇒ 簿顶变 ⇒ 推快照 tick
        }

        return updates;
    }

    /// <summary>
    /// 攻击方吃对手簿：方向 = 买吃卖侧 / 卖吃买侧；<b>跳过本账户</b>（自成交防护）；
    /// <b>可吃边界 = min/max(限价, 庄家报价对手价)</b>——合成做市商报价与簿合并成「有效最优价」，
    /// 簿档位只有优于或等于报价才被吃（劣价挂单不被劣价成交，等行情穿越它走庄家路径——价格连贯）；
    /// 档位有序 ⇒ 首个出界即止；每档成交量 = 双方余量取小、<b>成交价 = 挂单价（maker 价）</b>。调用方须持锁。
    /// </summary>
    private List<(string AccountId, SimOrderUpdate Update)> WalkBook(
        SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        var book = BookOf(instrument.InstrumentId);
        if (book.Count == 0 || entry.Remaining <= 0) return updates;   // 空簿 fast path——单客户端零额外路径

        // 交易所模式：簿即全部市场——可吃边界 = 限价本身（市价无界吃全簿）；walk 模式才受庄家报价约束
        var quote = _exchange ? null : Market.GetQuote(instrument.InstrumentId);
        var (bound, within) = BookCrossBound(entry.Order, quote);
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

            // 双方各自 ApplyFill（TradeId 从各自账户序列铸）+ 各自记账；被动方先落（maker 语义上先存在）
            var up = passiveState.Orders.ApplyFill(passiveEntry, volume, price, _clock.TimeOfDay);
            ApplyFillAccounting(passiveState, instrument, up);
            updates.Add((passiveState.AccountId, up));
            if (!SimOrderManager.CanCancel(passiveEntry)) book.Remove(be.AccountId, be.OrderRef);

            var ua = state.Orders.ApplyFill(entry, volume, price, _clock.TimeOfDay);
            ApplyFillAccounting(state, instrument, ua);
            updates.Add((state.AccountId, ua));

            // 交易所模式：成交即行情——最新价/累计量更新 + 立即外发含新簿顶的 tick；成交 tick 落盘（真实交易消息）
            if (_exchange)
            {
                var tick = Market.RecordTrade(instrument.InstrumentId, price, volume);
                if (tick is not null) Persist(() => _store.SaveTickAsync(tick));
            }
        }

        return updates;
    }

    /// <summary>FOK 干跑用：对手簿可吃量（<b>跳本账户</b>——与 <see cref="WalkBook"/> 一致的可吃边界与自成交跳过）。调用方须持锁。</summary>
    private int BookFillable(SimAccountState state, SimOrderEntry entry, SimInstrument instrument)
    {
        var book = BookOf(instrument.InstrumentId);
        if (book.Count == 0) return 0;

        var quote = _exchange ? null : Market.GetQuote(instrument.InstrumentId);
        var (bound, within) = BookCrossBound(entry.Order, quote);
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
    /// 簿可吃边界：买 ⇒ 档位价 ≤ min(限价, 报价卖一)；卖 ⇒ 档位价 ≥ max(限价, 报价买一)。
    /// 市价单无自有限价（只受报价约束）；无报价 ⇒ 只受限价。返回谓词（档位有序 ⇒ break 友好）。
    /// </summary>
    private static (double Bound, Func<double, bool> Within) BookCrossBound(CtpOrder order, SimQuote? quote)
    {
        var isBuy = order.Direction == CtpDirection.Buy;
        var limit = order.PriceType == CtpOrderPriceType.Limit ? order.LimitPrice : double.NaN;
        var quoteOpposite = isBuy ? quote?.AskPrice : quote?.BidPrice;

        double bound;
        if (isBuy)
        {
            bound = double.MaxValue;
            if (!double.IsNaN(limit)) bound = Math.Min(bound, limit);
            if (quoteOpposite is { } askBound) bound = Math.Min(bound, askBound);
            return (bound, p => p <= bound + 1e-9);
        }
        bound = double.MinValue;
        if (!double.IsNaN(limit)) bound = Math.Max(bound, limit);
        if (quoteOpposite is { } bidBound) bound = Math.Max(bound, bidBound);
        return (bound, p => p >= bound - 1e-9);
    }

    /// <summary>
    /// 簿内交叉扫（<see cref="AdvanceMarket"/> 第一步）：价格漂移使最优买 ≥ 最优卖（不同账户）⇒ 互吃。
    /// 成交价 = <b>maker 价（先挂者价）</b>；同账户锁单在顶无法不违价格优先地解 ⇒ 跳过该簿（文档化）。
    /// 调用方须持锁。
    /// </summary>
    private void CrossMatchBooks(List<(string AccountId, SimOrderUpdate Update)> perAccount)
    {
        foreach (var instrumentId in _books.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())   // 确定性
        {
            var book = _books[instrumentId];
            if (book.Count == 0) continue;
            var meta = InstrumentOf(instrumentId);
            if (meta is null) continue;

            while (true)
            {
                var bids = book.BidSide();
                var asks = book.AskSide();
                if (bids.Count == 0 || asks.Count == 0) break;
                var bid = bids[0];
                var ask = asks[0];
                if (bid.Price < ask.Price - 1e-9) break;   // 未交叉

                var bidState = Find(bid.AccountId);
                var bidEntry = bidState?.Orders.Find(bid.OrderRef);
                var askState = Find(ask.AccountId);
                var askEntry = askState?.Orders.Find(ask.OrderRef);
                if (bidState is null || bidEntry is null || !SimOrderManager.CanCancel(bidEntry))
                {
                    book.Remove(bid.AccountId, bid.OrderRef);   // 陈旧条目清除（防死循环）
                    continue;
                }
                if (askState is null || askEntry is null || !SimOrderManager.CanCancel(askEntry))
                {
                    book.Remove(ask.AccountId, ask.OrderRef);
                    continue;
                }
                if (string.Equals(bid.AccountId, ask.AccountId, StringComparison.OrdinalIgnoreCase))
                    break;   // 同账户锁单在顶——价格优先与自成交跳过不可兼得，跳过整簿

                var volume = Math.Min(bidEntry.Remaining, askEntry.Remaining);
                var maker = bid.Seq < ask.Seq ? bid : ask;      // 先挂者为 maker
                var price = meta.RoundToTick(maker.Price);

                var ub = bidState.Orders.ApplyFill(bidEntry, volume, price, _clock.TimeOfDay);
                ApplyFillAccounting(bidState, meta, ub);
                perAccount.Add((bidState.AccountId, ub));
                if (!SimOrderManager.CanCancel(bidEntry)) book.Remove(bid.AccountId, bid.OrderRef);

                var ua = askState.Orders.ApplyFill(askEntry, volume, price, _clock.TimeOfDay);
                ApplyFillAccounting(askState, meta, ua);
                perAccount.Add((askState.AccountId, ua));
                if (!SimOrderManager.CanCancel(askEntry)) book.Remove(ask.AccountId, ask.OrderRef);

                if (_exchange) Market.RecordTrade(instrumentId, price, volume);   // 防御性：今日该路径仅 walk 模式可达
            }
        }
    }

    // —— 做市商义务监测（交易所约束层——bots 即 Lean 策略后仅存的交易所侧职能）——
    // 2026-09-28 起做市/噪声行为迁出服务端（labs leanalgorithms + 实盘 tab 的普通客户端账户），
    // 服务端回归纯交易所：撮合/风控/账本/行情/上市闸门/认证 + 本监测面。
    // 监测按**约定账户名** <see cref="MarketMakerAccountId"/>（SIM-MM）：做市商用该账户登录接入即可被监测；
    // 报价自律（价差/量/持仓限额/移心）在策略侧（market-maker.lean.cs）。

    /// <summary>指定做市商账户（约定名——用该账户登录的客户端即被视为做市商，纳入义务监测）。</summary>
    public const string MarketMakerAccountId = "SIM-MM";

    private Timer? _mmMonitorTimer;

    /// <summary>
    /// 启动做市商义务监测（host 在交易所模式启动时调用；监测对象 = <see cref="MarketMakerAccountId"/> 账户）。
    /// 监测面：每合约双边报价时间占比（触限单边期豁免不计入分母）、价差/量合规、持仓限额、违约进入次数；
    /// <see cref="QueryMakerObligations"/> 查询。合约集从做市账户的活单派生（监测「实际在报价的合约」）。
    /// </summary>
    public void StartMakerObligationMonitor()
    {
        lock (_gate)
        {
            var interval = Math.Max(50, _options.MmMonitorIntervalMs);
            _mmMonitorTimer ??= new Timer(_ => ObserveMakerObligations(), null, interval, interval);
        }
    }

    /// <summary>做市商义务监测报告（每合约一行：双边报价占比/当前是否双边/是否达标/违约次数/持仓）。</summary>
    public IReadOnlyList<MmObligationReport> QueryMakerObligations()
    {
        lock (_gate) return _mmMonitor.Report(_options.MmMinQuoteRatio);
    }

    /// <summary>义务监测采样（定时器回调；在 <c>_gate</c> 内统计——只读账/单/持仓，无变异）。</summary>
    private void ObserveMakerObligations()
    {
        lock (_gate)
        {
            var mm = Find(MarketMakerAccountId);
            if (mm is null) return;   // 指定做市账户未接入——无可监测
            var limit = Math.Max(1, _options.MmPositionLimit);
            var elapsed = Math.Max(1, _options.MmMonitorIntervalMs);

            // 合约集从做市账户的活单派生（监测实际在报价的合约）
            var instruments = new SortedSet<string>(mm.Orders.All
                .Where(SimOrderManager.CanCancel)
                .Select(e => e.Order.InstrumentId), StringComparer.OrdinalIgnoreCase);

            foreach (var instrumentId in instruments)
            {
                var instrument = InstrumentOf(instrumentId);
                if (instrument is null) continue;

                var book = mm.Positions.Find(instrumentId);
                var longLots = book?.Long.Total ?? 0;
                var shortLots = book?.Short.Total ?? 0;

                // 做市商自己的开仓向挂单 = 它的报价（价差/量义务按此判定）
                double ownBid = 0, ownAsk = 0;
                var bidVol = 0;
                var askVol = 0;
                foreach (var e in mm.Orders.All)
                {
                    if (!SimOrderManager.CanCancel(e)) continue;
                    if (!string.Equals(e.Order.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SimRiskEngine.IsClose(e.Order.OffsetFlag)) continue;
                    if (e.Order.Direction == CtpDirection.Buy)
                    {
                        ownBid = ownBid == 0 ? e.Order.LimitPrice : Math.Max(ownBid, e.Order.LimitPrice);
                        bidVol += e.Remaining;
                    }
                    else
                    {
                        ownAsk = ownAsk == 0 ? e.Order.LimitPrice : Math.Min(ownAsk, e.Order.LimitPrice);
                        askVol += e.Remaining;
                    }
                }

                var twoSided = ownBid > 0 && ownAsk > 0;
                var tick = instrument.PriceTick > 0 ? instrument.PriceTick : 1;
                var spreadOk = !twoSided || ownAsk - ownBid <= Math.Max(2, _options.MmMaxSpreadTicks) * tick + 1e-9;
                var volumeOk = !twoSided || (bidVol >= Math.Max(1, _options.MmMinQuoteVolume) && askVol >= Math.Max(1, _options.MmMinQuoteVolume));
                var positionOk = Math.Max(longLots, shortLots) <= limit;
                var exempt = longLots >= limit || shortLots >= limit;   // 触限单边报价期豁免（不计入占比分母——真实做市协议的持仓豁免条款）

                _mmMonitor.Observe(instrumentId, twoSided, spreadOk, volumeOk, positionOk, exempt, longLots, shortLots, elapsed);
            }
        }
    }

    // —— 交易所模式行情源回调（引擎经 BindBook 持有；全部在 _gate 内调用）——

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
    /// 合约解析：**交易所模式先过上市闸门**（未上市 ⇒ 拒 90002——上市表是唯一可交易集合，上架是显式决定）；
    /// 然后精确 → 按品种派生 → 合成（或按 <see cref="SimOptions.AcceptUnknownInstruments"/> 拒绝——walk 沙盒语义）。
    /// </summary>
    private SimInstrument ResolveInstrument(CtpOrderRequest request)
    {
        var id = request.InstrumentId ?? "";
        if (_exchange && !_listedIds.Contains(id))
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

        if (!_options.AcceptUnknownInstruments)
            throw new CtpBusinessException(SimErrorCodes.UnknownInstrument,
                $"未知合约 {id}：仿真合约表中无此品种，且未启用未知合约兜底。");

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
