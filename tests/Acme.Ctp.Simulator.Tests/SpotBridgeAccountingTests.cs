using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 批 4 接线（2026-10-07）的交易所侧验收：BurnSpotPosition（提币烧毁——「存入股份」的逆操作）+
/// 铸造台账（桥对账的 DB 侧真相）+ 落盘续用（is_spot 列——修前 ax 重启后退化为期货）。
/// </summary>
public class SpotBridgeAccountingTests
{
    private const string Instrument = "ax001";

    private static async Task<SimTradingCore> NewCoreAsync(string? dbPath = null)
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = dbPath, ListedInstruments = new[] { Instrument }, InitialBalance = 1_000_000 },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        return core;
    }

    [Fact]
    public async Task 充币零成本_平价卖出全额变现()
    {
        // 批 6 经济定调钉：充币持仓 venue 侧成本 = 0（币不是现金换的）⇒ 平价（@100）卖 10 手
        // → 已实现盈亏 = 全额成交价 1000（「充币卖出全额变现」——用户模型的核心语义；
        // 买方保证金是真实现金 backing，margin 锁定保资金守恒）。
        var core = await NewCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 10);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, 100, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 10, 100, instrument: Instrument));

        var mm = core.QueryAccount("mm");
        Assert.Equal(1000, mm.CloseProfit, 2);   // 10 手 × @100 全额（充币成本 0）
        Assert.DoesNotContain(core.QueryPositions("mm", Instrument), p => p.TotalPosition > 0);
    }

    [Fact]
    public async Task 提币烧毁_持仓减保证金消耗价款带走()
    {
        var core = await NewCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 10);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, 100, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 10, 100, instrument: Instrument));
        // buyer 买入 10 手：全额保证金 1000、成本 100（last=100 ⇒ 浮盈 0）

        var before = core.QueryAccount("buyer");
        Assert.Equal(1000, before.CurrMargin, 2);

        core.BurnSpotPosition(Instrument, "buyer", 10);

        var after = core.QueryAccount("buyer");
        Assert.Equal(0, after.CurrMargin, 2);                        // 保证金解除
        Assert.Equal(before.Balance - 1000, after.Balance, 2);       // 价款随币走（静态权益扣减——非平仓变现）
        Assert.Equal(before.Available, after.Available, 2);          // 净效果 Available 不变（解除+扣减对冲）
        Assert.DoesNotContain(core.QueryPositions("buyer", Instrument), p => p.TotalPosition > 0);
        Assert.Equal(0, core.GetTotalLongVolume(Instrument));        // 币离所 ⇒ 供给回收
    }

    [Fact]
    public async Task 烧毁量超持仓_拒绝90007_无残留()
    {
        var core = await NewCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 5);

        var ex = Assert.Throws<CtpBusinessException>(() => core.BurnSpotPosition(Instrument, "mm", 6));
        Assert.Equal(SimErrorCodes.CloseVolumeExceeds, ex.ErrorId);

        // 拒绝是无残留的：持仓/台账不动
        Assert.Contains(core.QueryPositions("mm", Instrument), p => p.TotalPosition == 5);
        Assert.Equal(5, core.GetSpotMintedVolumes(Instrument)["mm"]);
    }

    [Fact]
    public async Task 铸造台账下限零_买入后烧毁()
    {
        // 「买入后提币」：buyer 台账 0、持仓 10（买的）——烧得掉（venue 持仓闸门），台账 clamp 0 不为负。
        // 对账等价性：链侧 IOU(buyer)=0 == 台账(buyer)=0 ⇒ 不补铸（币是买的不是充的——提走后无铸造义务）。
        var core = await NewCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 10);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, 100, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 10, 100, instrument: Instrument));

        core.BurnSpotPosition(Instrument, "buyer", 10);   // 台账 0−10 → clamp 0（与链侧 IOU clamp 对称）

        Assert.False(core.GetSpotMintedVolumes(Instrument).TryGetValue("buyer", out var minted) && minted != 0);
        Assert.Equal(10, core.GetSpotMintedVolumes(Instrument)["mm"]);   // mm 的铸造义务不受 buyer 提币影响
    }

    [Fact]
    public async Task 非现货烧毁被拒_镜像充币()
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { "ag2610" } },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();

        Assert.Throws<CtpBusinessException>(() => core.BurnSpotPosition("ag2610", "mm", 1));
    }

    private static async Task<SimTradingCore> NewZeroFaucetCoreAsync()
    {
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { Instrument }, InitialBalance = 0 },
            new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        return core;
    }

    [Fact]
    public async Task 计价币充值入账_现金到账_提现闸门()
    {
        var core = await NewZeroFaucetCoreAsync();
        core.Login("SIM", "u1", "pw");

        // 充 100 AQ（1 AQ = 1 现金）→ StaticBalance 100
        core.CreditQuoteDeposit("u1", 10_000_000_000UL);
        var after = core.QueryAccount("u1");
        Assert.Equal(100, after.Balance - after.PositionProfit, 2);

        // 提 30 AQ → 现金 70
        core.WithdrawQuoteCash("u1", 3_000_000_000UL);
        after = core.QueryAccount("u1");
        Assert.Equal(70, after.Balance - after.PositionProfit, 2);

        // 超可提量（70）→ 拒 90005
        var ex = Assert.Throws<CtpBusinessException>(() => core.WithdrawQuoteCash("u1", 71_000_000_000UL));
        Assert.Equal(SimErrorCodes.InsufficientFunds, ex.ErrorId);

        // 台账（充值 100 − 提现 30 = 70 AQ 原子）
        Assert.Equal(7_000_000_000L, core.GetQuoteMintedAtoms()["u1"]);
    }

    [Fact]
    public async Task 计价币提现_保证金占用不可动()
    {
        // 闸门 = 静态可用：买入占用保证金（100% 现货）后，被占部分不可提现——动它会破坏资金守恒。
        var core = await NewZeroFaucetCoreAsync();
        core.CreditQuoteDeposit("u1", 2_000_000_000_000UL);   // 20000 现金
        core.CreditQuoteDeposit("u2", 2_000_000_000_000UL);
        core.CreditSpotPosition(Instrument, "mm", 10);          // 卖方供给
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, 100, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("u1", SimHarness.Limit(CtpDirection.Buy, 10, 100, instrument: Instrument));   // u1 买 10 手锁 1000

        var u1 = core.QueryAccount("u1");
        Assert.Equal(1000, u1.CurrMargin, 2);                  // 1000 被占用（静态可用 = 20000−1000 = 19000）
        core.WithdrawQuoteCash("u1", 100_000_000_000UL);       // 提 1000 AQ ✓ 过闸
        var ex = Assert.Throws<CtpBusinessException>(() => core.WithdrawQuoteCash("u1", 19_000_000_000_000UL));   // 19000 AQ > 剩余可用 → 拒
        Assert.Equal(SimErrorCodes.InsufficientFunds, ex.ErrorId);
    }

    [Fact]
    public async Task 落盘重启_铸造台账与is_spot续用()
    {
        // is_spot 不落盘（修前）⇒ 重启后 ax001 退化为期货 ⇒ Credit/Burn 全被「非现货」拒——现货功能静默死亡。
        var dir = Path.Combine(Path.GetTempPath(), $"aether-spot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var dbPath = Path.Combine(dir, "ctp-sim.db");
            var first = await NewCoreAsync(dbPath);
            first.CreditSpotPosition(Instrument, "mm", 10);
            first.Dispose();   // 同步 flush

            var second = await NewCoreAsync(dbPath);
            Assert.True(second.TryGetInstrument(Instrument)!.IsSpot);               // is_spot 列 round-trip
            Assert.Equal(10, second.GetSpotMintedVolumes(Instrument)["mm"]);       // 铸造台账续用
            Assert.Contains(second.QueryPositions("mm", Instrument), p => p.TotalPosition == 10);   // 持仓续用
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
