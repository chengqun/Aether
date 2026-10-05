using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// maker/taker 差异化手续费（BTC 对标，2026-10-05）——zx001（SIM 自有品种：maker 2bp / taker 5bp / 乘数 10）。
/// 攻击成交付 taker、被动成交付 maker（按<b>成交价</b> × 实际量）；下单冻结按 taker 上限（最坏情形），
/// maker 成交实际费用更低 ⇒ 差额留存冻结池、终态/撤销释放——资金守恒恒等式不破（AssertConserved）。
/// </summary>
public class MakerTakerFeeTests
{
    private const string Instrument = "zx001";
    private const double MakerRate = 0.0002, TakerRate = 0.0005, Multiple = 10;
    private const double Bid = 3799, Ask = 3801;   // maker 账户双边种子价

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
    public async Task 跨账户成交_taker吃maker挂单_双方费率分计()
    {
        var core = await NewCoreAsync();

        // alice 挂买 3800（高于种子 bid 3799 ⇒ 顶档）；bob 市价卖 1 吃顶档 ⇒ 成交价 3800（maker 价）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 3800, instrument: Instrument));
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Sell, 1, instrument: Instrument));

        var alice = core.QueryAccount("alice");
        var bob = core.QueryAccount("bob");
        Assert.Equal(3800 * Multiple * 1 * MakerRate, alice.Commission, 6);   // 被动成交 → maker 费率
        Assert.Equal(3800 * Multiple * 1 * TakerRate, bob.Commission, 6);     // 攻击成交 → taker 费率
        AssertConserved(core, "alice");
        AssertConserved(core, "bob");
    }

    [Fact]
    public async Task 攻击单部分成交残量入簿再被吃_taker与maker费分段()
    {
        var core = await NewCoreAsync();

        // rest 挂卖 5@3800（优于种子 ask 3801）；t 限价买 8@3800：吃 rest 的 5（taker 费）+ 残 3 入簿；
        // hit 市价卖 3 吃 t 的残量（t 转为被动 → maker 费）
        core.InsertOrder("rest", SimHarness.Limit(CtpDirection.Sell, 5, 3800, instrument: Instrument));
        core.InsertOrder("t", SimHarness.Limit(CtpDirection.Buy, 8, 3800, instrument: Instrument));
        core.InsertOrder("hit", SimHarness.Market(CtpDirection.Sell, 3, instrument: Instrument));

        var t = core.QueryAccount("t");
        // 5 手 taker（@3800）+ 3 手 maker（@3800，残量被动成交）——同一张单两种角色费率分段
        Assert.Equal(3800 * Multiple * 5 * TakerRate + 3800 * Multiple * 3 * MakerRate, t.Commission, 6);
        // 全成终态：冻结按 taker 上限（3800×10×8×taker），实际费用更低 ⇒ 差额已释放 ⇒ 冻结归零
        Assert.Equal(0, t.FrozenCash, 6);
        var order = core.QueryOrders("t").Single(o => o.InstrumentId == Instrument);
        Assert.Equal(8, order.VolumeTraded);
        Assert.Equal('0', (char)order.OrderStatusRaw);   // 全部成交
        AssertConserved(core, "t");
        AssertConserved(core, "rest");
        AssertConserved(core, "hit");
    }

    [Fact]
    public async Task 撤销挂单_冻结按taker上限_零手续费全额释放()
    {
        var core = await NewCoreAsync();

        // 永不吃到的挂单（买 3790 < 种子 bid 3799——同侧低价，不吃不交叉）
        core.InsertOrder("po", SimHarness.Limit(CtpDirection.Buy, 1, 3790, instrument: Instrument));
        var mid = core.QueryAccount("po");
        Assert.Equal(3790 * Multiple * 1 * TakerRate, mid.FrozenCash, 6);   // 冻结按 taker 上限（最坏情形）
        Assert.Equal(0, mid.Commission, 6);

        var o = core.QueryOrders("po").Single();
        core.CancelOrder("po", new CtpOrderRef(SimHarness.Broker, "po", o.FrontId, o.SessionId, o.OrderRef, o.ExchangeId));

        var after = core.QueryAccount("po");
        Assert.Equal(0, after.FrozenCash, 6);          // 冻结全额释放（含 over-freeze 差额——flat 品种差额为 0，此例主要钉释放路径）
        Assert.Equal(0, after.Commission, 6);
        AssertConserved(core, "po");
    }

    [Fact]
    public async Task 差异化费率落盘_重启续用()
    {
        var db = Path.Combine(Path.GetTempPath(), $"ctpsim-mt-{Guid.NewGuid():N}.db");
        var opts = new SimOptions { DatabasePath = db, ListedInstruments = new[] { Instrument } };
        try
        {
            using (var core = new SimTradingCore(opts, new SimClock(() => SimHarness.TradingNow)))
            {
                await core.InitializeAsync();   // 播种 zx001（含 maker/taker 费率）并落盘
                var zx = core.QueryInstruments().Single(i => i.InstrumentId == Instrument);
                Assert.Equal(0.0002, zx.MakerCommissionRate!.Value, 10);
                Assert.Equal(0.0005, zx.TakerCommissionRate!.Value, 10);
            }
            using (var core2 = new SimTradingCore(opts, new SimClock(() => SimHarness.TradingNow)))
            {
                await core2.InitializeAsync();  // 幂等：表非空不再播种——从库加载（旧列 NULL → 回退单一费率；本例非 NULL → 原值）
                var zx = core2.QueryInstruments().Single(i => i.InstrumentId == Instrument);
                Assert.Equal(0.0002, zx.MakerCommissionRate!.Value, 10);
                Assert.Equal(0.0005, zx.TakerCommissionRate!.Value, 10);
            }
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    /// <summary>资金守恒恒等式（镜像 CrossAccountMatchingTests）：Available 与 Balance 两条恒等式。</summary>
    private static void AssertConserved(SimTradingCore core, string accountId)
    {
        var a = core.QueryAccount(accountId);
        var balance = a.Balance - (a.CurrMargin + a.FrozenMargin + a.FrozenCash);
        Assert.True(Math.Abs(balance - a.Available) < 0.05,
            $"{accountId}: Available 恒等式破坏（Balance-CurrMargin-Frozen-Frozen={balance} vs Available={a.Available}）");
        var staticBalance = SimHarness.InitialBalance - a.Commission + a.CloseProfit;
        Assert.True(Math.Abs(a.Balance - (staticBalance + a.PositionProfit)) < 0.05,
            $"{accountId}: Balance 恒等式破坏（Balance={a.Balance} vs 初始-手续费+平仓盈亏+持仓盈亏={staticBalance + a.PositionProfit}）");
    }
}
