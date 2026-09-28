using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 做市商义务监测（交易所约束层——2026-09-28 bots 即 Lean 策略后服务端仅存的交易所侧职能）：
/// 做市/噪声行为已迁出服务端（labs leanalgorithms 客户端策略），服务端按**约定账户名 SIM-MM**
/// 监测做市商的报价合规（双边报价占比/价差/量/持仓限额/违约计数）。报价自律（点差/量/持仓/移心）
/// 在策略侧（market-maker.lean.cs）——交易所只监测不强制（真实做市协议：交易所监测+处罚，市商自律）。
/// </summary>
public class MarketMakerObligationTests
{
    private static SimOptions MonitorOptions(int monitorMs = 1000) => new()
    {
        TickIntervalMs = 0,
        DatabasePath = null,
        MarketMode = SimMarketMode.Exchange,
        MmMonitorIntervalMs = monitorMs,
        ListedInstruments = new[] { SimHarness.Instrument },
    };

    [Fact]
    public async Task 指定做市账户双边挂单纳入监测()
    {
        var core = await SimHarness.NewCoreAsync(MonitorOptions(monitorMs: 20), injectQuote: false);
        core.StartMakerObligationMonitor();
        core.ActivateMarket(SimHarness.Instrument);

        // 「做市商」（约定账户 SIM-MM）双边挂单——服务端视角与外部客户端经 wire 下单等价
        core.InsertOrder(SimTradingCore.MarketMakerAccountId, SimHarness.Limit(CtpDirection.Buy, 5, 6988));
        core.InsertOrder(SimTradingCore.MarketMakerAccountId, SimHarness.Limit(CtpDirection.Sell, 5, 6992));

        await Task.Delay(200);   // 几个采样周期

        var report = core.QueryMakerObligations().Single(r => r.InstrumentId == SimHarness.Instrument);
        Assert.True(report.TwoSidedNow);
        Assert.True(report.QuoteTimeRatio > 0.5);
        Assert.True(report.MeetsRatio);
        Assert.Equal(0, report.Violations);
    }

    [Fact]
    public async Task 普通账户挂单不进监测()
    {
        var core = await SimHarness.NewCoreAsync(MonitorOptions(monitorMs: 20), injectQuote: false);
        core.StartMakerObligationMonitor();
        core.ActivateMarket(SimHarness.Instrument);

        // 非 SIM-MM 账户的双边挂单——不进义务监测（只监测约定做市账户）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 5, 6988));
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 5, 6992));

        await Task.Delay(150);

        Assert.Empty(core.QueryMakerObligations());
    }
}
