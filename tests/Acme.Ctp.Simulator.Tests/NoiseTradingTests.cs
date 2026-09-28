using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 噪声交易者（模拟成交）：随机吃做市流动力的程序化参与者（账户 <see cref="SimTradingCore.NoiseAccountId"/>）——
/// 真实下单、真实成交、真实记账；成交 ⇒ 最新价动 ⇒ 做市商移心重挂 ⇒ 盘口持续刷新。
/// 持仓/OI 有界（净持仓软钳 + 有仓平仓无仓开仓）；无对手流动性 ⇒ 本轮跳过。
/// </summary>
public class NoiseTradingTests
{
    private static SimOptions NoiseOptions(bool noise = true, int maxLots = 3) => new()
    {
        TickIntervalMs = 0,
        DatabasePath = null,
        MarketMode = SimMarketMode.Exchange,
        MarketMakerBot = true,
        NoiseTrading = noise,
        NoiseMaxLots = maxLots,
        PasswordIterations = 1000,
    };

    private static async Task<SimTradingCore> NewNoiseCoreAsync(bool noise = true, int maxLots = 3)
    {
        var core = await SimHarness.NewCoreAsync(NoiseOptions(noise, maxLots), injectQuote: false);
        core.ActivateMarket(SimHarness.Instrument);   // 做市进场（双边报价）
        return core;
    }

    [Fact]
    public async Task 噪声交易真实成交并推动行情()
    {
        var core = await NewNoiseCoreAsync();
        var ticks = new Collector<CtpTick>();
        core.TickProduced += ticks.Add;
        var baseline = core.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice;   // 6990（昨结算）

        for (var i = 0; i < 10; i++) core.RunNoiseTrade();

        // 真实成交发生：噪声账户有报单（市价吃单——对手是做市商的挂单）
        var noiseOrders = core.QueryOrders(SimTradingCore.NoiseAccountId, SimHarness.Instrument);
        Assert.NotEmpty(noiseOrders);

        // 行情被推动：过程中至少有一帧最新价离开基准（真实成交驱动；末帧可能均值回归回基准——锯齿本性）
        Assert.Contains(ticks.Snapshot, t => t.InstrumentId == SimHarness.Instrument && Math.Abs(t.LastPrice - baseline) > 1e-9);
        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.True(last.Volume > 0, "累计成交量必须来自真实成交");

        // 做市商是真实的对手方：其账户出现成交记录（多头或空头）
        var mmPositions = core.QueryPositions(SimTradingCore.MarketMakerAccountId, SimHarness.Instrument);
        Assert.Contains(mmPositions, p => p.TotalPosition > 0);

        // 做市商围绕新最新价移心：盘口双边仍在
        var q = core.Market.GetQuote(SimHarness.Instrument)!.Value;
        Assert.True(q.BidPrice > 0 && q.AskPrice > 0);
        Assert.True(q.AskPrice - q.BidPrice <= 4 + 1e-9, "移心后价差仍受义务钳制");
    }

    [Fact]
    public async Task 噪声净持仓有界_自动回归()
    {
        var core = await NewNoiseCoreAsync(maxLots: 3);

        for (var i = 0; i < 40; i++) core.RunNoiseTrade();

        // 净持仓软钳：|long − short| ≤ 2×maxLots（随机方向可越界一批，越界即反转）
        var book = core.QueryPositions(SimTradingCore.NoiseAccountId, SimHarness.Instrument);
        var net = book.Where(p => p.PosiDirection == CtpPosiDirection.Long).Sum(p => p.TotalPosition)
                    - book.Where(p => p.PosiDirection == CtpPosiDirection.Short).Sum(p => p.TotalPosition);
        Assert.True(Math.Abs(net) <= 6, $"噪声净持仓越界：{net}");

        // OI 有界（有仓平仓无仓开仓——不是无限开仓累积）
        var gross = book.Sum(p => p.TotalPosition);
        Assert.True(gross <= 12, $"噪声总持仓（OI 贡献）越界：{gross}");
    }

    [Fact]
    public async Task 无对手流动性时本轮跳过()
    {
        // 做市机器人关 ⇒ 无人挂单 ⇒ 噪声市价单无对手——静默跳过（不硬吃 90016）
        var core = await SimHarness.NewCoreAsync(NoiseOptions(noise: true) with { MarketMakerBot = false }, injectQuote: false);
        core.ActivateMarket(SimHarness.Instrument);   // 激活行情（但无做市报价）

        core.RunNoiseTrade();
        core.RunNoiseTrade();

        Assert.Empty(core.QueryOrders(SimTradingCore.NoiseAccountId, null));
    }

    [Fact]
    public async Task 噪声交易固定种子可复现()
    {
        // 同种子 ⇒ 同一串噪声决策（合约/方向/量）——确定性测试的基础
        var a = await NewNoiseCoreAsync();
        var b = await NewNoiseCoreAsync();
        for (var i = 0; i < 8; i++) { a.RunNoiseTrade(); b.RunNoiseTrade(); }

        Assert.Equal(
            a.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice,
            b.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice, 6);
    }
}
