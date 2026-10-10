using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 批 4 现货上币验收：CTP 现货映射（买=开多 / 卖=开空 / 平多平空——**2026-10-10 开空解禁**，双向持仓与期货一致）。
/// 品种 `ax`（Aether 币）：1 手 = 1 币、MarginRate=1.0（全额占用）、IsSpot=true。
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
    public async Task 开空成功_现货双向持仓_2026_10_10解禁()
    {
        // 开空解禁（用户拍板「现货也可以做空，一个品种就够」）：现货与期货一致的双向持仓。
        // 资金安全：开空同额保证金全程锁定（与期货同构）；场内 OI 超链上真币 = 纸面头寸（期货常态）；
        // 提币侧链上 custody 约束兜底（链拒后对账自愈回滚，零损失）。
        var core = await NewSpotCoreAsync();

        // shorter 挂卖开空 @100，buyer 买开吃单 → 成交双方持仓
        core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Sell, 1, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 1, Price, instrument: Instrument));

        var shorts = core.QueryPositions("shorter", Instrument);
        Assert.Contains(shorts, p => p.TotalPosition == 1 && p.PosiDirection == CtpPosiDirection.Short);
        var longs = core.QueryPositions("buyer", Instrument);
        Assert.Contains(longs, p => p.TotalPosition == 1 && p.PosiDirection == CtpPosiDirection.Long);
        // 开空全额保证金（MarginRate=1.0）：1 手 @100 占 100
        Assert.Equal(100, core.QueryAccount("shorter").CurrMargin, 2);
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
    public async Task 提币闸门只认多头库存_空头持仓不能提()
    {
        // 开空解禁（2026-10-10）后撮合可增 OI（买开 vs 卖开成交双方 Long+1/Short+1）——
        // 资金安全由保证金锁定保证（与期货同构）；链上真币纪律由**提币侧**兜底：
        // BurnSpotPosition 只烧 Long 持仓（空头持仓不能变出币）；凭空多头的提币受链上 custody
        // 余额约束（链拒后对账自愈回滚）——「场内随便开、链上真币一个不会多」。
        var core = await NewSpotCoreAsync(supplyLimit: null);
        core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Sell, 5, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 5, Price, instrument: Instrument));

        // 撮合新造 OI：buyer 多头 5（非充币铸造）、shorter 空头 5
        Assert.Equal(5, core.GetTotalLongVolume(Instrument));
        // 空头不能提币：shorter 无 Long 库存 ⇒ 烧毁拒
        var ex = Assert.Throws<CtpBusinessException>(() => core.BurnSpotPosition(Instrument, "shorter", 1));
        Assert.Equal(SimErrorCodes.CloseVolumeExceeds, ex.ErrorId);
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
