using Acme.Ctp.Domain;
using QuantConnect.Orders;

namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// CTP 持仓快照（净额形态，供 offset 决策）。CTP 按多/空方向分别记录今昨仓——快照聚合后提供 <see cref="Net"/>。
/// </summary>
public sealed record CtpPositionSnapshot(
    string ExchangeId,
    int LongTotal, int LongToday, int LongYd,
    int ShortTotal, int ShortToday, int ShortYd)
{
    /// <summary>净持仓：正=净多，负=净空，0=空仓或锁仓对冲后净零。</summary>
    public int Net => LongTotal - ShortTotal;

    public bool IsFlat => LongTotal == 0 && ShortTotal == 0;
}

/// <summary>
/// Lean Order → CtpOrderRequest 纯映射（P2 真实下单核心决策）。
/// 入参全部注入（无 session/网关依赖），可脱离 CTP 单测。<para/>
/// 规则（用户拍板）：
/// ①市价单（Market/MarketOnOpen）→ 对手价限价单（买 Ask/卖 Bid，fallback LastPrice→PreSettlement→拒单）；
/// ②开仓封顶 <see cref="MapInput.MaxOrderVolume"/>（保险丝），平仓绝不封顶；
/// ③平仓 offset：SHFE/INE 平昨优先（CloseYesterday 不足再 CloseToday），其他交易所直接 Close；
/// ④反转单（平仓+开仓混合）拒绝——本仓策略只走 Long↔Flat，不应出现。
/// </summary>
public static class CtpOrderRequestMapper
{
    /// <summary>上期所系（须显式平今/平昨）。</summary>
    private static bool IsShfeFamily(string exchangeId)
        => exchangeId.Equals("SHFE", StringComparison.OrdinalIgnoreCase)
           || exchangeId.Equals("INE", StringComparison.OrdinalIgnoreCase);

    public sealed record MapInput(
        OrderDirection Direction,
        OrderType OrderType,
        decimal LimitPrice,          // Limit 单用（order.Price）
        decimal Quantity,            // 有符号：正=买、负=卖
        string InstrumentId,
        CtpPositionSnapshot? Position,
        CtpTick? LatestTick,
        string BrokerId,
        string InvestorId,
        int MaxOrderVolume);

    public sealed record MapResult(IReadOnlyList<CtpOrderRequest> Requests, string? Error);

    public static MapResult Map(MapInput input)
    {
        var empty = new MapResult(Array.Empty<CtpOrderRequest>(), null);

        // —— 数量：非零整数（CTP 手数）——
        var qty = input.Quantity;
        if (qty == 0 || qty != Math.Floor(qty))
            return new MapResult(Array.Empty<CtpOrderRequest>(), $"CTP 手数须为非零整数: {qty}");
        var vol = (int)Math.Abs(qty);
        var isBuy = qty > 0;
        if ((isBuy && input.Direction != OrderDirection.Buy) || (!isBuy && input.Direction != OrderDirection.Sell))
            return new MapResult(Array.Empty<CtpOrderRequest>(), $"方向不一致: Quantity={qty} Direction={input.Direction}");

        // —— 价格：市价（含 MOO——Base 类型不拦 MOO，当市价处理）→ 对手价限价；Limit → 指定价 ——
        double price;
        switch (input.OrderType)
        {
            case OrderType.Market or OrderType.MarketOnOpen:
                var tick = input.LatestTick;
                if (tick == null)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "无最新行情（tick 为空），拒单——对手价限价单需要行情");
                price = isBuy ? tick.AskPrice : tick.BidPrice;
                if (price <= 0) price = tick.LastPrice;
                if (price <= 0) price = tick.PreSettlement;
                if (price <= 0)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "行情 Ask/Bid/Last/PreSettlement 均无效，拒单");
                break;
            case OrderType.Limit:
                if (input.LimitPrice <= 0)
                    return new MapResult(Array.Empty<CtpOrderRequest>(), "限价单价格无效");
                price = (double)input.LimitPrice;
                break;
            default:
                return new MapResult(Array.Empty<CtpOrderRequest>(), $"不支持的订单类型: {input.OrderType}");
        }

        var direction = isBuy ? CtpDirection.Buy : CtpDirection.Sell;
        var pos = input.Position;

        // —— Offset 决策 ——
        // 持仓快照缺失 / 空仓 / 净方向与订单同向（加仓）→ 开仓（封顶保险丝）
        if (pos == null || pos.IsFlat || (pos.Net > 0 && isBuy) || (pos.Net < 0 && !isBuy))
        {
            var capped = vol;
            if (input.MaxOrderVolume > 0 && capped > input.MaxOrderVolume)
                capped = input.MaxOrderVolume;
            if (capped <= 0)
                return new MapResult(Array.Empty<CtpOrderRequest>(), "手数上限封顶后为 0，拒单");
            return new MapResult(new[] { BuildRequest(input, direction, CtpOffsetFlag.Open, price, capped) }, null);
        }

        // —— 反向：平仓（绝不封顶）——
        var net = Math.Abs(pos.Net);
        if (vol > net)
            return new MapResult(Array.Empty<CtpOrderRequest>(),
                $"反转单（平 {net} 手 + 开 {vol - net} 手）暂不支持——请先平仓再开仓");

        // 平多用 Long 侧记录，平空用 Short 侧
        var (yd, today) = pos.Net > 0 ? (pos.LongYd, pos.LongToday) : (pos.ShortYd, pos.ShortToday);

        if (IsShfeFamily(pos.ExchangeId))
        {
            // 平昨优先：closeVolume <= Yd → 单腿 CloseYesterday；否则拆 CloseYesterday(Yd)+CloseToday(余)
            var ydUsed = Math.Min(vol, yd);
            var todayUsed = vol - ydUsed;
            if (todayUsed > today)
                return new MapResult(Array.Empty<CtpOrderRequest>(),
                    $"持仓今昨数据不一致（需平今 {todayUsed} > 实际今仓 {today}），拒单防错腿");

            var list = new List<CtpOrderRequest>(2);
            if (ydUsed > 0) list.Add(BuildRequest(input, direction, CtpOffsetFlag.CloseYesterday, price, ydUsed));
            if (todayUsed > 0) list.Add(BuildRequest(input, direction, CtpOffsetFlag.CloseToday, price, todayUsed));
            if (list.Count == 0)
                return new MapResult(Array.Empty<CtpOrderRequest>(), "平仓量计算为 0，拒单");
            return new MapResult(list, null);
        }

        // 非 SHFE 系（DCE/CZCE/CFFEX/GFEX）：直接 Close（交易所自动分配今昨）
        return new MapResult(new[] { BuildRequest(input, direction, CtpOffsetFlag.Close, price, vol) }, null);
    }

    private static CtpOrderRequest BuildRequest(MapInput input, CtpDirection direction, CtpOffsetFlag offset, double price, int volume) => new()
    {
        BrokerId = input.BrokerId,
        InvestorId = input.InvestorId,
        InstrumentId = input.InstrumentId,
        Direction = direction,
        OffsetFlag = offset,
        PriceType = CtpOrderPriceType.Limit,
        LimitPrice = price,
        Volume = volume,
        TimeCondition = CtpTimeCondition.GFD,
    };
}
