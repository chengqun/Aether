using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 单笔报单的仿真内部记录。
/// <para>
/// 之所以需要它而不是直接拿 <see cref="CtpOrder"/> 当状态：Domain DTO 里**没有**有效期类型
/// （<see cref="CtpTimeCondition"/>）与「该单还冻着多少钱」这两项——它们是仿真柜台的内部账，
/// 不进业务契约。对外只暴露 <see cref="Order"/> 快照。
/// </para>
/// </summary>
public sealed class SimOrderEntry
{
    internal SimOrderEntry(CtpOrder order, CtpTimeCondition timeCondition, double frozenMargin, double frozenFee)
    {
        Order = order;
        TimeCondition = timeCondition;
        FrozenMarginTotal = frozenMargin;
        FrozenFeeTotal = frozenFee;
        FrozenMarginRemaining = frozenMargin;
        FrozenFeeRemaining = frozenFee;
    }

    /// <summary>对外可见的报单快照（每次状态变化换一个新实例）。</summary>
    public CtpOrder Order { get; internal set; }

    /// <summary>有效期类型（IOC/FOK/GFD）——撮合后决定残量去向。</summary>
    public CtpTimeCondition TimeCondition { get; }

    /// <summary>下单时冻结的保证金总额（成交摊销的分母）。</summary>
    public double FrozenMarginTotal { get; }

    /// <summary>下单时冻结的手续费总额。</summary>
    public double FrozenFeeTotal { get; }

    /// <summary>尚冻结的保证金（成交按比例消耗、撤单/残量作废一次性退还）。</summary>
    public double FrozenMarginRemaining { get; internal set; }

    /// <summary>尚冻结的手续费。</summary>
    public double FrozenFeeRemaining { get; internal set; }

    /// <summary>未成交手数。</summary>
    public int Remaining => Math.Max(0, Order.VolumeTotalOriginal - Order.VolumeTraded);

    /// <summary>是否仍在队列中（可继续撮合、可撤）。</summary>
    public bool IsLive => SimOrderStatus.IsLive(Order.OrderStatusRaw);

    /// <summary>取出并清零尚冻结的金额（撤单 / 残量作废时退还）。</summary>
    internal (double Margin, double Fee) DrainFrozen()
    {
        var m = FrozenMarginRemaining;
        var f = FrozenFeeRemaining;
        FrozenMarginRemaining = 0;
        FrozenFeeRemaining = 0;
        return (m, f);
    }

    /// <summary>
    /// 按「成交手数 / 报单手数」摊算并消耗冻结额（终态时剩余冻结恰好归零 ⇒ 资金守恒）。
    /// </summary>
    internal (double Margin, double Fee) ConsumeFrozen(int fillVolume)
    {
        var original = Math.Max(1, Order.VolumeTotalOriginal);
        var m = Math.Min(FrozenMarginRemaining, FrozenMarginTotal * fillVolume / original);
        var f = Math.Min(FrozenFeeRemaining, FrozenFeeTotal * fillVolume / original);
        FrozenMarginRemaining = Math.Max(0, FrozenMarginRemaining - m);
        FrozenFeeRemaining = Math.Max(0, FrozenFeeRemaining - f);
        return (m, f);
    }
}

/// <summary>
/// 一次报单状态变化（要推给客户端的一帧）。
/// <para>
/// 金额三项的语义必须分清——混用会让资金账户多算/少算：
/// <list type="bullet">
/// <item><see cref="ConsumedFrozenMargin"/>/<see cref="ConsumedFrozenFee"/>：**被本次成交消耗**的冻结额。
/// 开仓时前者转为持仓占用保证金（<c>CurrMargin</c>）、后者转为已扣手续费。</item>
/// <item><see cref="ReleasedFrozenMargin"/>/<see cref="ReleasedFrozenFee"/>：**退还**的冻结额（撤单 / IOC·FOK 残量作废），直接回可用资金。</item>
/// </list>
/// </para>
/// <para><see cref="Trade"/> 非空表示本次变化由成交驱动——核心会**先推 <c>OrderReceived</c> 再推
/// <c>TradeReceived</c>**（与真柜台回调顺序一致）。</para>
/// </summary>
public sealed record SimOrderUpdate(
    CtpOrder Order,
    CtpTrade? Trade = null,
    double ConsumedFrozenMargin = 0,
    double ConsumedFrozenFee = 0,
    double ReleasedFrozenMargin = 0,
    double ReleasedFrozenFee = 0);

/// <summary>
/// 报单管理器：状态机 + 报单/成交号铸造 + 冻结摊销。
/// <para>
/// <b>状态机</b>：<c>'3' 已报入(挂单)</c> →（成交）<c>'1' 部分成交</c> →（全成）<c>'0' 全部成交</c>；
/// 未成交残量按有效期收敛：GFD 继续挂单，IOC/FOK 残量 <c>'2'</c>/<c>'4'</c>，用户撤单 <c>'5'</c>。
/// </para>
/// <para>
/// <b>没有「已拒单」状态</b>：风控拒单发生在 <see cref="SimTradingCore"/> 分配报单号**之前**
/// （直接抛 <see cref="CtpBusinessException"/>——真柜台 <c>OrderInsertAsync</c> 的成功路径无错误通道，
/// 违约只能抛异常），故本管理器里不会出现被拒的报单。
/// </para>
/// <para>本类**非线程安全**——由 <see cref="SimTradingCore"/> 的信号量串行化。</para>
/// </summary>
public sealed class SimOrderManager
{
    private readonly Dictionary<string, SimOrderEntry> _byOrderRef = new(StringComparer.Ordinal);
    private readonly List<string> _insertionOrder = new();
    private long _tradeSeq;
    private int _sysSeq;

    /// <summary>全部报单（按下单先后，历史顺序稳定）。</summary>
    public IReadOnlyList<SimOrderEntry> All => _insertionOrder.Select(r => _byOrderRef[r]).ToList();

    public SimOrderEntry? Find(string? orderRef)
        => orderRef is not null && _byOrderRef.TryGetValue(orderRef, out var e) ? e : null;

    /// <summary>铸造报单系统号（真柜台由交易所分配，仿真用递增序号）。</summary>
    public string NextOrderSysId() => (++_sysSeq).ToString(CultureInfo.InvariantCulture);

    /// <summary>铸造成交号。</summary>
    public string NextTradeId() => (++_tradeSeq).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 接受一笔报单：初始状态 <c>'3' 未成交还在队列中</c>。
    /// 冻结额由调用方（核心 + 风控）算好传入。
    /// </summary>
    public SimOrderUpdate Accept(
        CtpOrderRequest request,
        string orderRef,
        int frontId,
        int sessionId,
        string exchangeId,
        string insertTime,
        double frozenMargin,
        double frozenFee)
    {
        var order = new CtpOrder
        {
            InstrumentId = request.InstrumentId,
            ExchangeId = exchangeId,
            OrderRef = orderRef,
            FrontId = frontId,
            SessionId = sessionId,
            OrderSysId = NextOrderSysId(),
            Direction = request.Direction,
            OffsetFlag = request.OffsetFlag,
            PriceType = request.PriceType,
            LimitPrice = request.LimitPrice,
            VolumeTotalOriginal = request.Volume,
            VolumeTraded = 0,
            VolumeTotal = request.Volume,
            OrderStatusRaw = SimOrderStatus.NoTradeQueueing,
            InsertTime = insertTime,
            UpdateTime = insertTime,
            StatusMsg = "已报入",
        };
        var entry = new SimOrderEntry(order, request.TimeCondition, frozenMargin, frozenFee);
        AddEntry(orderRef, entry);
        return new SimOrderUpdate(order);
    }

    /// <summary>
    /// 登记一笔报单并保证 <c>orderRef ↔ entry</c> 一一对应。
    /// <para>
    /// 必须先查重再插列表：若同一 <c>orderRef</c> 被登记两次（历史恢复后单号序列没续上就会发生，
    /// 见 <see cref="SimTradingCore.InitializeAsync"/> 的单号恢复），旧写法会把键插进
    /// <c>_insertionOrder</c> 两遍 → <see cref="All"/>（及查询）**同一笔报单回两行**。
    /// </para>
    /// </summary>
    private void AddEntry(string orderRef, SimOrderEntry entry)
    {
        if (_byOrderRef.ContainsKey(orderRef))
        {
            _byOrderRef[orderRef] = entry;
            return;
        }
        _byOrderRef[orderRef] = entry;
        _insertionOrder.Add(orderRef);
    }

    /// <summary>
    /// 记一笔成交：推进 <c>VolumeTraded</c>/<c>VolumeTotal</c> 与状态，并摊算冻结额的消耗/退还。
    /// 成交手数会被裁剪到实际未成交量内（防御性）。
    /// </summary>
    public SimOrderUpdate ApplyFill(SimOrderEntry entry, int volume, double price, string time)
    {
        var before = entry.Order;
        var fill = Math.Min(Math.Max(0, volume), entry.Remaining);
        if (fill <= 0) return new SimOrderUpdate(before);

        var traded = before.VolumeTraded + fill;
        var remaining = before.VolumeTotalOriginal - traded;

        // 残量是否留在队列：IOC/FOK 成交后残量不再挂单（'2'），GFD 继续挂（'1'）
        var staysQueued = remaining > 0 && entry.TimeCondition == CtpTimeCondition.GFD;

        var status = remaining == 0
            ? SimOrderStatus.AllTraded
            : staysQueued ? SimOrderStatus.PartTradedQueueing : SimOrderStatus.PartTradedNotQueueing;

        var order = before with
        {
            VolumeTraded = traded,
            VolumeTotal = staysQueued ? remaining : 0,
            OrderStatusRaw = status,
            UpdateTime = time,
            StatusMsg = SimOrderStatus.Describe(status),
        };
        entry.Order = order;

        var (consumedMargin, consumedFee) = entry.ConsumeFrozen(fill);

        // 残量不再挂单（或已全成）⇒ 剩余冻结一并退还，保证终态冻结归零
        double releasedMargin = 0, releasedFee = 0;
        if (remaining == 0 || !staysQueued)
        {
            (releasedMargin, releasedFee) = entry.DrainFrozen();
        }

        var trade = new CtpTrade
        {
            InstrumentId = order.InstrumentId,
            OrderRef = order.OrderRef,
            TradeId = NextTradeId(),
            OrderSysId = order.OrderSysId,
            Direction = order.Direction,
            OffsetFlag = order.OffsetFlag,
            Price = price,
            Volume = fill,
            TradeTime = time,
        };

        return new SimOrderUpdate(order, trade, consumedMargin, consumedFee, releasedMargin, releasedFee);
    }

    /// <summary>
    /// 残量不再在队列中（IOC/FOK 未成部分作废）：
    /// 有成交过记 <c>'2' 部分成交不在队列</c>，否则 <c>'4' 未成交不在队列</c>。退还全部剩余冻结。
    /// </summary>
    public SimOrderUpdate? FinalizeNoQueue(SimOrderEntry entry, string time, string reason)
    {
        if (SimOrderStatus.IsTerminal(entry.Order.OrderStatusRaw)) return null;

        var status = entry.Order.VolumeTraded > 0
            ? SimOrderStatus.PartTradedNotQueueing
            : SimOrderStatus.NoTradeNotQueueing;

        entry.Order = entry.Order with
        {
            VolumeTotal = 0,
            OrderStatusRaw = status,
            UpdateTime = time,
            StatusMsg = reason,
        };
        var (m, f) = entry.DrainFrozen();
        return new SimOrderUpdate(entry.Order, null, 0, 0, m, f);
    }

    /// <summary>用户撤单：状态 <c>'5' 已撤销</c>、剩余量归零、退还全部剩余冻结。</summary>
    public SimOrderUpdate? Cancel(SimOrderEntry entry, string time, string reason = "已撤销")
    {
        if (SimOrderStatus.IsTerminal(entry.Order.OrderStatusRaw)) return null;

        entry.Order = entry.Order with
        {
            VolumeTotal = 0,
            OrderStatusRaw = SimOrderStatus.Canceled,
            UpdateTime = time,
            StatusMsg = reason,
        };
        var (m, f) = entry.DrainFrozen();
        return new SimOrderUpdate(entry.Order, null, 0, 0, m, f);
    }

    /// <summary>该报单是否可撤（仍有未成交残量且在队列中）。</summary>
    public static bool CanCancel(SimOrderEntry entry)
        => SimOrderStatus.IsLive(entry.Order.OrderStatusRaw) && entry.Remaining > 0;

    /// <summary>
    /// 从落盘快照恢复一笔报单（**不**恢复冻结账——重启后挂单不再参与撮合，见核心的恢复逻辑）。
    /// <para>恢复同时把 <see cref="NextTradeId"/> 的序号推进到已落盘成交号之上（<paramref name="maxTradeId"/>）。</para>
    /// </summary>
    internal void Restore(CtpOrder order, long maxTradeId = 0)
    {
        if (_byOrderRef.ContainsKey(order.OrderRef)) return;
        var entry = new SimOrderEntry(order, CtpTimeCondition.GFD, 0, 0)
        {
            FrozenMarginRemaining = 0,
            FrozenFeeRemaining = 0,
        };
        AddEntry(order.OrderRef, entry);
        if (int.TryParse(order.OrderSysId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sys) && sys > _sysSeq)
            _sysSeq = sys;
        if (maxTradeId > _tradeSeq) _tradeSeq = maxTradeId;
    }
}
