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
    public async Task 充币成本锚定BasePrice_等价平仓盈亏为零()
    {
        // 2026-10-07 修正回归钉：旧版 CreditSpotPosition 把 Cost 当「均价」写（应按 Σ(价×乘数×量) 求和约定），
        // AvgOpenPrice = Cost/(乘数×Total) 错乘数倍 → 平多盈亏凭空放大（@100 平 10 手凭空 +900）。
        var core = await NewCoreAsync();
        core.CreditSpotPosition(Instrument, "mm", 10);
        core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 10, 100, CtpOffsetFlag.Close, CtpTimeCondition.GFD, Instrument));
        core.InsertOrder("buyer", SimHarness.Limit(CtpDirection.Buy, 10, 100, instrument: Instrument));

        var mm = core.QueryAccount("mm");
        Assert.Equal(0, mm.CloseProfit, 2);   // 平 @100 = 成本锚 100 ⇒ 零盈亏（求和约定下的正确口径）
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
