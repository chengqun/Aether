using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// ax001 品种验收（2026-10-10 终态：**AQ 计价的保证金合约**——与期货完全一致的双向持仓 +
/// 10% 保证金（10 倍杠杆）。币通道已退役：交易所单资产化只收付计价币 AQ（CreditQuoteDeposit 唯一充值通道），
/// coin 不进交易所；场内头寸全部纸面（撮合产生），与链上 coin 无关。
/// 1 手 = 1 币名义、PriceTick=0.01、maker 2bp / taker 5bp。
/// </summary>
public class SpotInstrumentTests
{
    private const string Instrument = "ax001";
    private const double Price = 100;

    private static async Task<SimTradingCore> NewCoreAsync()
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { Instrument }, InitialBalance = 0 },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        return core;
    }

    [Fact]
    public async Task 买开多_10倍杠杆保证金_持仓正确()
    {
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("mm", 100_000_000_000UL);
        core.CreditQuoteDeposit("buyer", 100_000_000_000UL);

        // maker 挂卖开空 @100（开空解禁——供给侧），buyer 买开 10 手吃单
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 10, Price, instrument: Instrument));

        // 10% 保证金：10 手 × @100 × 1 × 0.1 = 100（与期货 MarginRate=0.1 同构——10 倍杠杆）
        Assert.Equal(100, core.QueryAccount("buyer").CurrMargin, 2);
        var positions = core.QueryPositions("buyer", Instrument);
        Assert.Contains(positions, p => p.TotalPosition == 10 && p.PosiDirection == CtpPosiDirection.Long);
        // 空头侧对称：maker 开空同样 10% 保证金
        Assert.Equal(100, core.QueryAccount("mm").CurrMargin, 2);
    }

    [Fact]
    public async Task 开空成功_现货与期货一致的双向持仓()
    {
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("shorter", 100_000_000_000UL);
        core.CreditQuoteDeposit("buyer", 100_000_000_000UL);

        // shorter 挂卖开空 @100，buyer 买开吃单 → 成交双方持仓
        core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Sell, 1, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 1, Price, instrument: Instrument));

        var shorts = core.QueryPositions("shorter", Instrument);
        Assert.Contains(shorts, p => p.TotalPosition == 1 && p.PosiDirection == CtpPosiDirection.Short);
        var longs = core.QueryPositions("buyer", Instrument);
        Assert.Contains(longs, p => p.TotalPosition == 1 && p.PosiDirection == CtpPosiDirection.Long);
    }

    [Fact]
    public async Task 平空盈亏_现金结算_保证金释放()
    {
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("shorter", 100_000_000_000UL);
        core.CreditQuoteDeposit("buyer", 100_000_000_000UL);

        // shorter 开空 @100（buyer 吃）
        core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Sell, 1, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 1, Price, instrument: Instrument));

        // 价格跌到 90：buyer 挂卖平多 @90，shorter 买平 @90 吃它 → 空头盈利 (100−90)×1 = 10
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Sell, 1, 90, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("shorter", SimHarness.Limit(CtpDirection.Buy, 1, 90, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        var shorter = core.QueryAccount("shorter");
        Assert.Equal(10, shorter.CloseProfit, 2);
        Assert.Equal(0, shorter.CurrMargin, 2);   // 平空后保证金释放
        Assert.DoesNotContain(core.QueryPositions("shorter", Instrument), p => p.TotalPosition > 0);
    }

    [Fact]
    public async Task 卖平多_释放保证金与盈亏()
    {
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("mm", 100_000_000_000UL);
        core.CreditQuoteDeposit("buyer", 100_000_000_000UL);

        // 买开 → 高位卖平：盈利 = (120−100)×3
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 3, Price, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 3, Price, instrument: Instrument));
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 3, 120, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Sell, 3, 120, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        var buyer = core.QueryAccount("buyer");
        Assert.Equal(0, buyer.CurrMargin, 2);   // 平多后保证金释放
        Assert.Equal(3 * 20, buyer.CloseProfit, 2);
        Assert.DoesNotContain(core.QueryPositions("buyer", Instrument), p => p.TotalPosition > 0);
    }

    [Fact]
    public async Task 唯一充值通道是计价币_1AQ等于1现金()
    {
        // 币通道退役后 CreditQuoteDeposit 是唯一的链上→场内通道（桥 AQ 对账驱动）。
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("user", 1000_00000000UL);   // 1000 AQ

        var account = core.QueryAccount("user");
        Assert.Equal(1000, account.Balance, 2);   // 1 AQ = 1 现金
        Assert.Equal(1000, account.Available, 2);
    }

    [Fact]
    public async Task 现货交易往返_经济语义正确()
    {
        var core = await NewCoreAsync();
        core.CreditQuoteDeposit("mm", 1_000_000_000_000UL);   // mm 充 10000 AQ（做市本金）
        core.CreditQuoteDeposit("buyer", 1_000_000_000_000UL); // buyer 充 10000 AQ
        // 做市商双边挂单（开仓做市——开空解禁后无需币库存冷启动）
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 5, 99, instrument: Instrument));
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 5, 101, CtpOffsetFlag.Open, CtpTimeCondition.GFD, Instrument));

        // buyer 在 101 买 2 手（保证金 = 2 × 101 × 1 × 0.1 = 20.2）
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 2, 101, instrument: Instrument));
        Assert.Equal(20.2, core.QueryAccount("buyer").CurrMargin, 2);

        // 价格涨到 119：mm 挂买平空 @119（高价回购平空），buyer 卖平多 @119 吃它 → 盈利 = 2 × (119 − 101) = 36
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 2, 119, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Sell, 2, 119, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));

        var buyer = core.QueryAccount("buyer");
        Assert.Equal(0, buyer.CurrMargin, 2);
        Assert.Equal(36, buyer.CloseProfit, 2);
    }
}
