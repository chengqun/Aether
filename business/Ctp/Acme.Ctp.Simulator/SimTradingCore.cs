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

    private Timer? _feedTimer;
    private int _frontId = 1;
    private int _sessionId;

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

        Market = new SimMarketDataEngine(options, _clock, InstrumentOf);
        Market.TickGenerated += (_, tick) => TickProduced?.Invoke(tick);
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

    /// <summary>合成 tick（仅在 <see cref="SimOptions.ExposeSyntheticTicks"/> 时产生）。</summary>
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

        RecomputePositionProfit();
    }

    /// <summary>启动随机游走（<see cref="SimOptions.TickIntervalMs"/> 为 0 时不启动，由调用方手动推进）。</summary>
    public void StartMarketFeed()
    {
        if (_options.TickIntervalMs <= 0) return;
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
        StopMarketFeed();
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
    /// 仿真登录：不做任何网络/凭据校验（仿真柜台的目的是「连得上」而非「认证」），
    /// 只为该投资者建账并返回可被客户端 <c>int.Parse</c> 的 <c>MaxOrderRef</c>。
    /// </summary>
    public CtpLoginResult Login(string brokerId, string userId)
    {
        var accountId = AccountIdOf(userId);
        lock (_gate)
        {
            _sessionId = _sessionId == 0 ? 1 : _sessionId;
            var state = GetOrCreate(accountId);
            // MaxOrderRef 必须是可解析的整数：CtpTrader.LoginAsync 拿它做下一单号起点
            var maxOrderRef = state.NextOrderRefSeq.ToString(CultureInfo.InvariantCulture);
            var result = new CtpLoginResult(_clock.TradingDay, _clock.TimeOfDay, _frontId, _sessionId, maxOrderRef);

            Persist(() => _store.SaveAccountAsync(state.Account.ToSnapshot()));
            return result;
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

    /// <summary>查询合约元数据（仿真柜台独有的便利方法，非 <c>ICtpSession</c> 契约）。</summary>
    public IReadOnlyList<SimInstrument> QueryInstruments()
    {
        lock (_gate) return _instruments.Values.OrderBy(i => i.InstrumentId, StringComparer.Ordinal).ToList();
    }

    // —— 下单 / 撤单 ——

    /// <summary>
    /// 下单。**风控拒单抛 <see cref="CtpBusinessException"/>**——真柜台 <c>OrderInsertAsync</c> 的成功路径
    /// 不返回错误码（只回显 <c>OrderRef</c>/<c>RequestId</c>），异常是唯一能在 <c>ICtpSession</c> 接缝上表达拒单的通道。
    /// </summary>
    public CtpOrderInsertResult InsertOrder(string accountId, CtpOrderRequest request)
    {
        var updates = new List<SimOrderUpdate>();
        var result = default(CtpOrderInsertResult)!;

        lock (_gate)
        {
            var state = GetOrCreate(accountId);
            var instrument = ResolveInstrument(request);
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
            updates.Add(u);
            result = new CtpOrderInsertResult(orderRef, RequestIdOf(orderRef));

            updates.AddRange(Match(state, instrument));
            RecomputePositionProfitLocked();

            foreach (var update in updates) PersistOrder(state, update);
        }

        Raise(accountId, updates);
        return result;
    }

    /// <summary>
    /// 撤单。定位不到报单 ⇒ <see cref="SimErrorCodes.OrderNotFound"/>；
    /// 已终态 ⇒ <see cref="SimErrorCodes.OrderNotCancelable"/>（两者都抛 <see cref="CtpBusinessException"/>）。
    /// </summary>
    public void CancelOrder(string accountId, CtpOrderRef orderRef)
    {
        var updates = new List<SimOrderUpdate>();

        lock (_gate)
        {
            var state = Find(accountId) ?? throw new CtpBusinessException(SimErrorCodes.NotLoggedIn, "未登录，无法撤单。");
            var entry = state.Orders.Find(orderRef.OrderRef)
                ?? throw new CtpBusinessException(SimErrorCodes.OrderNotFound,
                    $"撤单失败：报单 {orderRef.OrderRef} 不存在。");
            if (!SimOrderManager.CanCancel(entry))
                throw new CtpBusinessException(SimErrorCodes.OrderNotCancelable,
                    $"撤单失败：报单 {orderRef.OrderRef} 当前状态「{SimOrderStatus.Describe(entry.Order.OrderStatusRaw)}」不可撤。");

            var u = state.Orders.Cancel(entry, _clock.TimeOfDay);
            if (u is not null)
            {
                state.Account.ReleaseForOrder(u.ReleasedFrozenMargin, u.ReleasedFrozenFee);
                updates.Add(u);
                RecomputePositionProfitLocked();
                PersistOrder(state, u);
            }
        }

        Raise(accountId, updates);
    }

    // —— 行情推进（撮合挂单） ——

    /// <summary>
    /// 推进一个 tick：随机游走 + 用新价重试全部挂单。
    /// 供定时器调用，也供单测手动驱动（<see cref="SimOptions.TickIntervalMs"/> = 0 时不自动跑）。
    /// </summary>
    public void AdvanceMarket()
    {
        var perAccount = new List<(string AccountId, SimOrderUpdate Update)>();

        lock (_gate)
        {
            Market.Advance();

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
    /// 激活合约的行情游走（订阅时调用）。
    /// <para><see cref="AdvanceMarket"/> 只推进已激活的合约——只订阅不激活则行情永远不来。</para>
    /// </summary>
    public void ActivateMarket(string instrumentId)
    {
        lock (_gate) Market.Activate(instrumentId);
    }

    /// <summary>手动注入行情（测试 / 未来「历史 tick 回放」用）。注入后立即重试挂单。</summary>
    public void InjectQuote(string instrumentId, double bid, double ask, double last)
    {
        var updates = new List<(string AccountId, SimOrderUpdate Update)>();
        lock (_gate)
        {
            Market.Inject(instrumentId, bid, ask, last);
            foreach (var state in _states.Values)
                foreach (var update in Match(state, instrument: null))
                    updates.Add((state.AccountId, update));
            RecomputePositionProfitLocked();
        }
        foreach (var group in updates.GroupBy(x => x.AccountId, StringComparer.OrdinalIgnoreCase))
            Raise(group.Key, group.Select(x => x.Update).ToList());
    }

    // —— 内部：撮合 ——

    /// <summary>
    /// 对某账户的全部活单尝试撮合（<paramref name="instrument"/> 非空时只撮该合约）——下单后与每 tick 共用。
    /// 调用方须持锁。返回的状态变化按发生次序排列。
    /// </summary>
    private List<SimOrderUpdate> Match(SimAccountState state, SimInstrument? instrument)
    {
        var updates = new List<SimOrderUpdate>();

        // 快照以避免撮合过程中集合被改动；顺序 = 下单先后（价格优先/时间优先的简化版）
        var live = state.Orders.All.Where(SimOrderManager.CanCancel).ToList();
        foreach (var entry in live)
        {
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

    /// <summary>合约解析：精确 → 按品种派生 → 合成（或按 <see cref="SimOptions.AcceptUnknownInstruments"/> 拒绝）。</summary>
    private SimInstrument ResolveInstrument(CtpOrderRequest request)
    {
        var id = request.InstrumentId ?? "";
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
