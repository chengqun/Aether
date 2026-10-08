using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 批 4 现货上币验收：CTP 现货映射（买=开多 100% 保证金 / 卖=平多 / **禁开空** / 供给上限 ≤ custody 链上余额）。
/// 品种 `ax`（Aether 币）：1 手 = 1 币、MarginRate=1.0、IsSpot=true。
/// </summary>
public class SpotInstrumentTests
{
    private const string Instrument = "ax001";
    private const double Multiple = 1;
    private const double Price = 100;

    private static async Task<SimTradingCore> NewSpotCoreAsync(int? supplyLimit = null)
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { Instrument }, InitialBalance = 1_000_000 },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        if (supplyLimit is { } limit)
            core.SetSupplyLimit(Instrument, limit);
        return core;
    }

    [Fact]
    public async Task 买开多_全额保证金_持仓正确()
    {
        var core = await NewSpotCoreAsync();
        var maker = "mm";

        // maker 充币 10 手（桥铸造 Long 持仓——绕过订单簿）后挂平多卖单 @100（供给侧）
        core.CreditSpotPosition(Instrument, maker, 10);
        core.InsertOrder(maker, SimHarness.Limit(CtpDirection.Sell, 10, Price, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        // buyer 买 3 手（开多——全额保证金 = 3 × 100 × 1 × 1.0 = 300）
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 3, Price, instrument: Instrument));

        var buyer = core.QueryAccount("buyer");
        Assert.Equal(3 * Price * Multiple * 1.0, buyer.CurrMargin, 2);   // 300 全额占用
        var positions = core.QueryPositions("buyer", Instrument);
        Assert.Contains(positions, p => p.TotalPosition == 3 && p.PosiDirection == CtpPosiDirection.Long);
    }

    [Fact]
    public async Task 开空被拒_现货禁SellOpen()
    {
        var core = await NewSpotCoreAsync();

        var ex = Assert.Throws<CtpBusinessException>(() =>
            core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Sell, 1, Price, instrument: Instrument)));

        Assert.Equal(SimErrorCodes.InvalidParameter, ex.ErrorId);
        Assert.Contains("禁", ex.ErrorMsg);
    }

    [Fact]
    public async Task 卖平多_释放保证金与盈亏()
    {
        var core = await NewSpotCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 10);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, Price, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 3, Price, instrument: Instrument));

        // maker 挂买单（buyer 在 120 平多 → 利润 = 3 × (120-100) = 60）。mm 的买是 Open——开多（不是平空，mm 无空头）。
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 3, 120, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Sell, 3, 120, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        var buyer = core.QueryAccount("buyer");
        Assert.Equal(0, buyer.CurrMargin, 2);   // 平多后全额释放
        Assert.True(buyer.CloseProfit > 0, $"平仓盈亏应为正：{buyer.CloseProfit}");
        var positions = core.QueryPositions("buyer", Instrument);
        Assert.DoesNotContain(positions, p => p.TotalPosition > 0);   // 仓位归零
    }

    [Fact]
    public async Task 供给约束_桥充币是唯一闸门_撮合不增OI()
    {
        // 现货禁开空 ⇒ 全部卖单必为平多 ⇒ 撮合只转移 OI 不增加——CreditSpotPosition 是唯一增量入口。
        // 桥充 5 手（= custody 余额 5），之后交易只能转移不能增。
        var core = await NewSpotCoreAsync(supplyLimit: null);
        core.CreditSpotPosition(Instrument, "mm", 5);
        core.SetSupplyLimit(Instrument, 5);

        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 5, Price, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 5, Price, instrument: Instrument));

        // buyer 持仓 5（= mm 的 5 转移过来——总 OI 不变）
        var buyer = core.QueryPositions("buyer", Instrument);
        Assert.Contains(buyer, p => p.TotalPosition == 5);
        // mm 持仓归零
        var mm = core.QueryPositions("mm", Instrument);
        Assert.DoesNotContain(mm, p => p.TotalPosition > 0);
        // 全市场总 Long = 5（不变——供给约束由 CreditSpotPosition 守门，撮合只转移）
        Assert.Equal(5, core.GetTotalLongVolume(Instrument));
    }

    [Fact]
    public async Task CreditSpotPosition_非现货被拒()
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { "ag2610" } },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();

        var ex = Assert.Throws<CtpBusinessException>(() => core.CreditSpotPosition("ag2610", "mm", 5));
        Assert.Contains("非现货", ex.ErrorMsg);
    }

    [Fact]
    public async Task 现货交易往返_经济语义正确()
    {
        var core = await NewSpotCoreAsync(supplyLimit: null);
        // 做市商充币 + 双边（卖侧=平多——充币铸造的持仓）
        core.CreditSpotPosition(Instrument, "mm", 5);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 5, 99, instrument: Instrument));
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 5, 101, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        // buyer 在 101 买 2 手（全额保证金 = 2 × 101 × 1 × 1.0 = 202）
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 2, 101, instrument: Instrument));

        var buyerAfterBuy = core.QueryAccount("buyer");
        Assert.Equal(202, buyerAfterBuy.CurrMargin, 2);

        // 价格涨到 120（做市商双边移到 119/121）
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 2, 119, instrument: Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Sell, 2, 119, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        var buyerAfterSell = core.QueryAccount("buyer");
        Assert.Equal(0, buyerAfterSell.CurrMargin, 2);
        // 平仓盈亏 = 2 × (119 - 101) = 36
        Assert.Equal(36, buyerAfterSell.CloseProfit, 2);
        // 经济语义：花 202 买入 → 收回 238（202 + 36 盈利）——差额 = 36 ✓
    }
}
