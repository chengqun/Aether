using Acme.Ctp.Domain;
using Acme.Trading.Runtime.Lean;
using QuantConnect.Orders;
using Xunit;

namespace Acme.Trading.Runtime.Tests;

/// <summary>
/// P2 真实下单核心映射规则单测（纯函数，零 CTP 依赖）：
/// 对手价限价 / 开仓保险丝（平仓不封顶）/ SHFE 平昨优先拆腿 / 反转单拒绝 / 无行情拒单。
/// </summary>
public class CtpOrderRequestMapperTests
{
    private static CtpTick Tick(double bid = 7000, double ask = 7002, double last = 7001, double preSettle = 6990) => new()
    {
        InstrumentId = "ag2610",
        BidPrice = bid,
        AskPrice = ask,
        LastPrice = last,
        PreSettlement = preSettle,
    };

    private static CtpPositionSnapshot Pos(string exchange = "DCE",
        int longT = 0, int longToday = 0, int longYd = 0,
        int shortT = 0, int shortToday = 0, int shortYd = 0)
        => new(exchange, longT, longToday, longYd, shortT, shortToday, shortYd);

    private static CtpOrderRequestMapper.MapInput Input(
        decimal quantity,
        CtpPositionSnapshot? pos = null,
        CtpTick? tick = null,
        int maxVolume = 1,
        OrderType type = OrderType.Market,
        OrderDirection? dir = null,
        decimal limitPrice = 0m,
        bool missingTick = false)
        => new(
            dir ?? (quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell),   // 默认按数量符号推导
            type, limitPrice, quantity, "ag2610", pos,
            missingTick ? null : tick ?? Tick(),
            "9999", "user01", maxVolume);

    [Fact]
    public void Buy_on_flat_position_opens_at_ask()
    {
        var r = CtpOrderRequestMapper.Map(Input(+2m, pos: null, maxVolume: 10));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Buy, req.Direction);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(CtpOrderPriceType.Limit, req.PriceType);
        Assert.Equal(7002, req.LimitPrice);       // 买用卖一（Ask）
        Assert.Equal(2, req.Volume);
        Assert.Equal("9999", req.BrokerId);
        Assert.Equal("user01", req.InvestorId);
    }

    [Fact]
    public void Sell_close_long_non_shfe_uses_close_at_bid()
    {
        var r = CtpOrderRequestMapper.Map(Input(-3m,
            pos: Pos("DCE", longT: 5, longToday: 2, longYd: 3), maxVolume: 1));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Sell, req.Direction);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);   // 非 SHFE 系直接 Close
        Assert.Equal(7000, req.LimitPrice);                  // 卖用买一（Bid）
        Assert.Equal(3, req.Volume);                         // 平仓不受手数上限（maxVolume=1 仍 3 手）
    }

    [Fact]
    public void Sell_close_long_shfe_splits_yd_first_then_today()
    {
        // 昨 3 今 2，平 4 → 平昨 3 + 平今 1（平昨优先）
        var r = CtpOrderRequestMapper.Map(Input(-4m,
            pos: Pos("SHFE", longT: 5, longToday: 2, longYd: 3)));

        Assert.Null(r.Error);
        Assert.Equal(2, r.Requests.Count);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, r.Requests[0].OffsetFlag);
        Assert.Equal(3, r.Requests[0].Volume);
        Assert.Equal(CtpOffsetFlag.CloseToday, r.Requests[1].OffsetFlag);
        Assert.Equal(1, r.Requests[1].Volume);
    }

    [Fact]
    public void Sell_close_long_shfe_all_yesterday_single_leg()
    {
        var r = CtpOrderRequestMapper.Map(Input(-2m,
            pos: Pos("INE", longT: 2, longToday: 0, longYd: 2)));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.CloseYesterday, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Buy_closes_short_position()
    {
        var r = CtpOrderRequestMapper.Map(Input(+2m,
            pos: Pos("DCE", shortT: 2, shortToday: 1, shortYd: 1), dir: OrderDirection.Buy));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpDirection.Buy, req.Direction);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Same_direction_adds_to_position_as_open()
    {
        // 已持多 2 手，再买 1 → 加仓（Open），走保险丝
        var r = CtpOrderRequestMapper.Map(Input(+1m,
            pos: Pos("DCE", longT: 2, longToday: 2), maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(1, req.Volume);
    }

    [Fact]
    public void Open_volume_is_capped_by_fuse()
    {
        var r = CtpOrderRequestMapper.Map(Input(+10m, pos: null, maxVolume: 1));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Open, req.OffsetFlag);
        Assert.Equal(1, req.Volume);              // 10 手封顶到 1
    }

    [Fact]
    public void Partial_close_only_closes_requested_volume()
    {
        // 持多 5，卖 2 → 只平 2（Close，非反转）
        var r = CtpOrderRequestMapper.Map(Input(-2m,
            pos: Pos("DCE", longT: 5, longToday: 5)));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(CtpOffsetFlag.Close, req.OffsetFlag);
        Assert.Equal(2, req.Volume);
    }

    [Fact]
    public void Reversal_order_is_rejected()
    {
        // 持多 5，卖 10 → 平 5 + 开 5 = 反转单，拒绝
        var r = CtpOrderRequestMapper.Map(Input(-10m,
            pos: Pos("DCE", longT: 5, longToday: 5)));

        Assert.NotNull(r.Error);
        Assert.Contains("反转", r.Error);
        Assert.Empty(r.Requests);
    }

    [Fact]
    public void MarketOnOpen_is_treated_as_market()
    {
        // Base 类型不拦 MOO（exchange hours 与 CTP 夜盘可能不匹配）——当市价处理走对手价
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.MarketOnOpen, maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(7002, req.LimitPrice);
    }

    [Fact]
    public void Limit_order_uses_order_price()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.Limit, limitPrice: 7050m, maxVolume: 5));

        Assert.Null(r.Error);
        var req = Assert.Single(r.Requests);
        Assert.Equal(7050, req.LimitPrice);
    }

    [Fact]
    public void Missing_tick_rejects()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, missingTick: true));

        Assert.NotNull(r.Error);
        Assert.Contains("行情", r.Error);
    }

    [Fact]
    public void Price_falls_back_to_last_then_presettlement()
    {
        // Ask/Bid 无效 → LastPrice
        var r1 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 7001), maxVolume: 5));
        Assert.Null(r1.Error);
        Assert.Equal(7001, r1.Requests[0].LimitPrice);

        // Last 也无效 → PreSettlement
        var r2 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 0, preSettle: 6990), maxVolume: 5));
        Assert.Null(r2.Error);
        Assert.Equal(6990, r2.Requests[0].LimitPrice);

        // 全无效 → 拒单
        var r3 = CtpOrderRequestMapper.Map(Input(+1m, tick: Tick(bid: 0, ask: 0, last: 0, preSettle: 0), maxVolume: 5));
        Assert.NotNull(r3.Error);
    }

    [Fact]
    public void Fractional_or_zero_quantity_rejects()
    {
        Assert.NotNull(CtpOrderRequestMapper.Map(Input(0m)).Error);
        Assert.NotNull(CtpOrderRequestMapper.Map(Input(1.5m)).Error);
    }

    [Fact]
    public void Unsupported_order_type_rejects()
    {
        var r = CtpOrderRequestMapper.Map(Input(+1m, type: OrderType.StopMarket));

        Assert.NotNull(r.Error);
        Assert.Contains("不支持的订单类型", r.Error);
    }

    [Fact]
    public void Direction_mismatch_rejects()
    {
        // 数量正（买）但 Direction 声明 Sell → 拒绝（防映射错乱）
        var r = CtpOrderRequestMapper.Map(Input(+1m, dir: OrderDirection.Sell));

        Assert.NotNull(r.Error);
    }
}
