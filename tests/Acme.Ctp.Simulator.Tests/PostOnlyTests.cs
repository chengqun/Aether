using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// post-only 限价单（本地协议扩展 TC '4'，BTC 对标 2026-10-05）：只入簿不吃单——插入时会交叉 ⇒ 整单拒收 90018
/// （拒在报单号分配/冻结之前，无残留状态）；后续被动成交走 maker 费率（zx001：maker 2bp / taker 5bp）。
/// </summary>
public class PostOnlyTests
{
    private const string Instrument = "zx001";
    private const double MakerRate = 0.0002, Multiple = 10;
    private const double Bid = 3799, Ask = 3801;

    private static async Task<SimTradingCore> NewCoreAsync()
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { Instrument } },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        core.InsertOrder("maker", SimHarness.Limit(CtpDirection.Buy, 30, Bid, instrument: Instrument));
        core.InsertOrder("maker", SimHarness.Limit(CtpDirection.Sell, 30, Ask, instrument: Instrument));
        return core;
    }

    [Fact]
    public async Task 不交叉_接受入簿_后续被动成交走maker费率()
    {
        var core = await NewCoreAsync();

        // 买 3799：与种子 bid 同价（同侧不交叉；ask 3801 > 3799 ⇒ 不会吃卖侧）⇒ 受理入簿
        core.InsertOrder("po", SimHarness.Limit(CtpDirection.Buy, 1, Bid, timeCondition: CtpTimeCondition.PostOnly, instrument: Instrument));
        var o = core.QueryOrders("po").Single();
        Assert.Equal('3', (char)o.OrderStatusRaw);   // 已报入挂单

        // 市价卖 31：价格时间优先——种子的 30@3799 先成，po 的 1@3799 后成（po 全程被动）
        core.InsertOrder("hit", SimHarness.Market(CtpDirection.Sell, 31, instrument: Instrument));

        var po = core.QueryAccount("po");
        // QueryAccount DTO 金额四舍五入到分（MidpointRounding.AwayFromZero）——3799×10×0.0002=7.598 → 7.60
        Assert.Equal(Math.Round(Bid * Multiple * 1 * MakerRate, 2, MidpointRounding.AwayFromZero), po.Commission, 6);
        Assert.Equal(1, core.QueryPositions("po").Sum(p => p.TotalPosition));
        Assert.Equal('0', (char)core.QueryOrders("po").Single().OrderStatusRaw);   // 被动全成
    }

    [Fact]
    public async Task 会交叉_整单拒收90018_无残留()
    {
        var core = await NewCoreAsync();

        // 买 3801 ≥ 种子 ask 3801 ⇒ 会立即吃单 ⇒ 整单拒收
        var ex = Assert.Throws<CtpBusinessException>(() =>
            core.InsertOrder("x", SimHarness.Limit(CtpDirection.Buy, 1, Ask, timeCondition: CtpTimeCondition.PostOnly, instrument: Instrument)));
        Assert.Equal(SimErrorCodes.PostOnlyWouldCross, ex.ErrorId);

        // 拒在报单号分配/冻结之前——无订单记录、零冻结（资金原封不动）
        Assert.Empty(core.QueryOrders("x"));
        var acct = core.QueryAccount("x");
        Assert.Equal(0, acct.FrozenCash, 6);
        Assert.Equal(SimHarness.InitialBalance, acct.Available, 6);
    }

    [Fact]
    public async Task postonly仅限限价_参数非法90011()
    {
        var core = await NewCoreAsync();

        var ex = Assert.Throws<CtpBusinessException>(() =>
            core.InsertOrder("x", SimHarness.Market(CtpDirection.Buy, 1, timeCondition: CtpTimeCondition.PostOnly, instrument: Instrument)));
        Assert.Equal(SimErrorCodes.InvalidParameter, ex.ErrorId);
        Assert.Empty(core.QueryOrders("x"));
    }

    [Fact]
    public async Task 自身挂单不算交叉_自成交跳过语义()
    {
        var core = await NewCoreAsync();

        // self 挂卖 3800（优于种子 ask）——随后同账户 post-only 买 3800：自己的卖单不算对手（自成交跳过语义）⇒ 受理
        core.InsertOrder("self", SimHarness.Limit(CtpDirection.Sell, 1, 3800, instrument: Instrument));
        core.InsertOrder("self", SimHarness.Limit(CtpDirection.Buy, 1, 3800, timeCondition: CtpTimeCondition.PostOnly, instrument: Instrument));

        var buy = core.QueryOrders("self").Single(o => o.Direction == CtpDirection.Buy);
        Assert.Equal('3', (char)buy.OrderStatusRaw);   // 受理入簿（未因自身卖单 90018）
        // 两者都挂着（不自成交）：无成交、无持仓
        Assert.Equal(0, core.QueryPositions("self").Sum(p => p.TotalPosition));
        Assert.Equal(0, core.QueryAccount("self").Commission, 6);
    }
}
