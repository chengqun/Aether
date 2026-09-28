using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 做市商义务（交易所约束层——真实做市协议条款的强制面；策略层机器人自便，约束层人人须守）：
/// 价差/量钳制、持仓限额闸门（触限停加仓侧 = 「超限禁开」+ 最简库存偏斜）、超限自动平仓减仓、
/// 义务监测统计（双边报价占比/违约计数/持仓豁免口径）、询价回应。
/// </summary>
public class MarketMakerObligationTests
{
    private static SimOptions MmOptions(
        int spread = 2, int maxSpread = 4, int volume = 5, int minVolume = 1,
        int posLimit = 200, int monitorMs = 1000) => new()
    {
        TickIntervalMs = 0,
        DatabasePath = null,
        MarketMode = SimMarketMode.Exchange,
        MarketMakerBot = true,
        NoiseTrading = false,   // 监测统计要的是「静置观测」——噪声定时器不参与（避免慢机上断言期落成交）
        ListedInstruments = new[] { SimHarness.Instrument },   // 交易所模式：上市表 = 可交易集合
        MmSpreadTicks = spread,
        MmMaxSpreadTicks = maxSpread,
        MmVolume = volume,
        MmMinQuoteVolume = minVolume,
        MmPositionLimit = posLimit,
        MmMonitorIntervalMs = monitorMs,
    };

    private static async Task<SimTradingCore> NewMmCoreAsync(
        int spread = 2, int maxSpread = 4, int volume = 5, int minVolume = 1,
        int posLimit = 200, int monitorMs = 1000)
    {
        var core = await SimHarness.NewCoreAsync(
            MmOptions(spread, maxSpread, volume, minVolume, posLimit, monitorMs), injectQuote: false);
        core.ActivateMarket(SimHarness.Instrument);
        return core;
    }

    [Fact]
    public async Task 报价价差受最大价差约束()
    {
        // 策略想要 ±10 tick，交易所只允许总差 4 tick（默认 MmSpreadTicks 会被钳到 2）
        var core = await NewMmCoreAsync(spread: 10, maxSpread: 4);

        var q = core.Market.GetQuote(SimHarness.Instrument)!.Value;
        Assert.Equal(6988, q.BidPrice, 6);   // 6990 − min(10, 4/2) × tick1
        Assert.Equal(6992, q.AskPrice, 6);
        Assert.Equal(4, q.AskPrice - q.BidPrice, 6);   // 总差 ≤ 最大价差
    }

    [Fact]
    public async Task 单侧报价量受最小量约束()
    {
        // 策略想挂 1 手，交易所要求单侧 ≥ 3 手
        var core = await NewMmCoreAsync(volume: 1, minVolume: 3);
        var ticks = new Collector<CtpTick>();
        core.TickProduced += ticks.Add;

        core.ActivateMarket(SimHarness.Instrument);   // 再触发一次拿最新帧

        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(3, last.BidVolume);
        Assert.Equal(3, last.AskVolume);
    }

    [Fact]
    public async Task 空头触限停挂卖侧_只报买边()
    {
        // 限额 5、单侧 5 手：用户一笔买就把机器人打到空头触限 ⇒ 停挂卖侧（只报买边——「超限禁开」+ 最简库存偏斜）
        var core = await NewMmCoreAsync(posLimit: 5);

        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 5));   // 吃机器人卖一 6992×5

        // 机器人空头 5 == 限额 ⇒ 卖侧被闸，只补买边
        var q = core.Market.GetQuote(SimHarness.Instrument)!.Value;
        Assert.Equal(0, q.AskPrice, 6);   // 卖侧空（真实 CTP 单边市场语义）
        Assert.True(q.BidPrice > 0);
        Assert.Equal(5, core.QueryPositions(SimTradingCore.MarketMakerAccountId, SimHarness.Instrument)
            .Single(p => p.PosiDirection == CtpPosiDirection.Short).TotalPosition);
        // 买边报价仍在（开仓向活单只有买）
        var mmLive = core.QueryOrders(SimTradingCore.MarketMakerAccountId, SimHarness.Instrument)
            .Where(o => o.OrderStatusRaw == SimOrderStatus.NoTradeQueueing).ToList();
        Assert.Single(mmLive);
        Assert.Equal(CtpDirection.Buy, mmLive[0].Direction);
    }

    [Fact]
    public async Task 超限自动平仓减仓_从对手侧买回()
    {
        // 限额 3、单侧 5 手：吃满后空头 5 > 3 ⇒ 机器人朝对手最优价挂平仓买单，把超额 2 手买回来
        var core = await NewMmCoreAsync(posLimit: 3, volume: 5);
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        // alice 先挂卖 6994×3（排在机器人卖一 6992 之后）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 3, 6994));
        // bob 市价买 5：价优先吃机器人 6992×5 ⇒ 机器人空头 5 超限 3
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 5));

        // 超限减仓：机器人 BUY CLOSE 2 @6994（钉 alice 的最优卖价）⇒ 立即交叉成交，从 alice 手里买回
        var mmPositions = core.QueryPositions(SimTradingCore.MarketMakerAccountId, SimHarness.Instrument);
        Assert.Equal(3, mmPositions.Single(p => p.PosiDirection == CtpPosiDirection.Short).TotalPosition);   // 5 − 2
        Assert.DoesNotContain(mmPositions, p => p.PosiDirection == CtpPosiDirection.Long && p.TotalPosition > 0);

        var aliceTrades = trades.Snapshot.Where(t => t.AccountId == "alice").Select(t => t.Trade).ToList();
        Assert.Equal(2, aliceTrades.Sum(t => t.Volume));   // alice 的挂单被机器人回购 2 手
        Assert.All(aliceTrades, t => Assert.Equal(6994, t.Price, 6));

        // bob 全成 @6992（机器人的卖一——价优先于 alice 的 6994）
        var bobTrades = trades.Snapshot.Where(t => t.AccountId == "bob").Select(t => t.Trade).ToList();
        Assert.Equal(5, bobTrades.Sum(t => t.Volume));
        Assert.All(bobTrades, t => Assert.Equal(6992, t.Price, 6));
    }

    [Fact]
    public async Task 义务监测统计双边报价占比与持仓()
    {
        var core = await SimHarness.NewCoreAsync(MmOptions(monitorMs: 20), injectQuote: false);
        core.StartMarketMakerBot();                 // 上市表（sim001）报价 + 监测定时器
        core.ActivateMarket(SimHarness.Instrument); // ag2610 增量进场

        await Task.Delay(200);   // 几个采样周期

        var report = core.QueryMakerObligations().Single(r => r.InstrumentId == SimHarness.Instrument);
        Assert.True(report.TwoSidedNow);
        Assert.True(report.QuoteTimeRatio > 0.5);
        Assert.True(report.MeetsRatio);
        Assert.Equal(0, report.Violations);
        Assert.Equal(0, report.LongLots);
        Assert.Equal(0, report.ShortLots);
    }

    [Fact]
    public async Task 询价触发即时双边报价()
    {
        // 尚未激活/未报价的合约：询价 ⇒ 机器人立即双边
        var core = await SimHarness.NewCoreAsync(MmOptions(), injectQuote: false);

        Assert.True(core.RequestQuote(SimHarness.Instrument));
        var mmLive = core.QueryOrders(SimTradingCore.MarketMakerAccountId, SimHarness.Instrument)
            .Where(o => o.OrderStatusRaw == SimOrderStatus.NoTradeQueueing).ToList();
        Assert.Equal(2, mmLive.Count);   // 双边各一

        // walk 模式无做市商 ⇒ 询价恒 false
        var walkCore = await SimHarness.NewCoreAsync();
        Assert.False(walkCore.RequestQuote(SimHarness.Instrument));
    }
}
