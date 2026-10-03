using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure.Sim;
using Acme.Ctp.Simulator;
using Acme.Ctp.Simulator.Storage;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 交易所模式（价格发现归簿——2026-10-03 起恒为唯一模式）：行情 = 订单簿顶 + 成交流、
/// 成交驱动最新价、市价单空簿拒单、市价残量作废（2026-09-28 起做市/噪声 = 外部客户端策略，服务端无内嵌机器人）。
/// ag2610 种子元数据：昨结算 6990 / tick 1 / 乘数 15 / 保证金率 12%（做市报价断言的确定性来源）。
/// </summary>
public class ExchangeModeTests
{
    /// <summary>交易所核心（已激活 ag2610、上市表 = [ag2610]——做市报价由外部客户端策略供给，服务端无内嵌机器人）。</summary>
    private static async Task<SimTradingCore> NewExchangeCoreAsync()
    {
        var core = await SimHarness.NewCoreAsync(SimHarness.Options(), seedMaker: false);
        core.ActivateMarket(SimHarness.Instrument);
        return core;
    }

    [Fact]
    public async Task 市价单对手侧空簿被拒()
    {
        var core = await NewExchangeCoreAsync();

        var ex = Assert.Throws<CtpBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 1)));
        Assert.Equal(SimErrorCodes.NoCounterparty, ex.ErrorId);

        // 拒单 = 未接受——bob 无报单无持仓
        Assert.Empty(core.QueryOrders("bob", null));
        Assert.Empty(core.QueryPositions("bob", null));
    }

    [Fact]
    public async Task 未上市合约下单被拒_即便品种可派生()
    {
        // 上市表 = 唯一可交易集合——rb2701 虽可从 rb 品种派生元数据，但未上架 ⇒ 拒 90002。
        // （上架是显式决定——「下单即合成合约」的沙盒语义已随 walk 模式删除。）
        var core = await NewExchangeCoreAsync();   // 上市表 = [ag2610]

        var ex = Assert.Throws<CtpBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Limit(CtpDirection.Buy, 1, 7000, instrument: "rb2701")));
        Assert.Equal(SimErrorCodes.UnknownInstrument, ex.ErrorId);
        Assert.Contains("未上市", ex.ErrorMsg);
        Assert.Empty(core.QueryOrders("bob", null));
    }

    [Fact]
    public async Task 成交tick落盘并跨重启恢复行情状态()
    {
        var db = Path.Combine(Path.GetTempPath(), $"sim-tick-{Guid.NewGuid():N}.db");
        try
        {
            var options = SimHarness.Options() with { DatabasePath = db };
            var core = new SimTradingCore(options);
            await core.InitializeAsync();
            core.ActivateMarket(SimHarness.Instrument);
            core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 2, 6992));   // 对手挂单（做市商角色——外部客户端语义）
            core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 2));           // 成交 @6992 ×2
            core.Dispose();                                                        // 同步 flush

            // 落盘可查：store 直读，最新一条 = 该笔成交的快照（真实交易消息）
            var store = SimStore.ForFile(db);
            await store.InitializeAsync();
            var ticks = await store.LoadRecentTicksAsync(SimHarness.Instrument, 10);
            Assert.NotEmpty(ticks);
            Assert.Equal(6992, ticks[^1].LastPrice, 6);
            Assert.Equal(2, ticks[^1].Volume);

            // 重启恢复：最新价从最后成交续上（不回昨结算基准 6990）
            var revived = new SimTradingCore(options);
            await revived.InitializeAsync();
            Assert.Equal(6992, revived.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice, 6);

            // 累计量延续：重启后激活的快照帧携带恢复后的累计成交量（不归零）
            var after = new Collector<CtpTick>();
            revived.TickProduced += after.Add;
            revived.ActivateMarket(SimHarness.Instrument);
            var snapshotTick = after.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
            Assert.Equal(2, snapshotTick.Volume);
        }
        finally { try { File.Delete(db); } catch { } }
    }

    [Fact]
    public async Task 未上市合约订阅不激活()
    {
        var core = await NewExchangeCoreAsync();

        Assert.False(core.ActivateMarket("rb2701"));   // 不激活 ⇒ 无行情状态
        Assert.Null(core.Market.GetQuote("rb2701"));
    }

    [Fact]
    public async Task 自己的挂单不算对手_市价单仍被拒()
    {
        var core = await NewExchangeCoreAsync();

        // bob 自己挂卖——自成交跳过 ⇒ 对 bob 的市价买而言仍无对手
        core.InsertOrder("bob", SimHarness.Limit(CtpDirection.Sell, 1, 7000));
        var ex = Assert.Throws<CtpBusinessException>(
            () => core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 1)));
        Assert.Equal(SimErrorCodes.NoCounterparty, ex.ErrorId);
    }

    [Fact]
    public async Task 挂单入簿即推tick_簿顶是真实挂单()
    {
        var core = await NewExchangeCoreAsync();
        var ticks = new Collector<CtpTick>();
        core.TickProduced += ticks.Add;

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Buy, 3, 7000));   // 同价档——顶档量聚合

        // 行情 = 簿顶（不再是合成 mid∓tick）：买一 7000 × 5，卖侧空（真实 CTP 单边市场语义）
        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(7000, last.BidPrice, 6);
        Assert.Equal(5, last.BidVolume);
        Assert.Equal(0, last.AskPrice, 6);
        Assert.Equal(0, last.AskVolume);
    }

    [Fact]
    public async Task 成交驱动最新价_累计量为真实成交()
    {
        var core = await NewExchangeCoreAsync();
        var ticks = new Collector<CtpTick>();
        core.TickProduced += ticks.Add;

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Buy, 3, 7000));
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Sell, 5));   // 吃掉两笔挂单（maker 价 7000）

        var last = ticks.Snapshot.Last(t => t.InstrumentId == SimHarness.Instrument);
        Assert.Equal(7000, last.LastPrice, 6);   // 最新价 = 成交价（不是游走值）
        Assert.Equal(5, last.Volume);            // 累计量 = 真实成交手数（不是随机累计）
        Assert.Equal(0, last.BidPrice, 6);       // 挂单全成离簿——买侧空
    }

    [Fact]
    public async Task 市价单无界吃全簿多档()
    {
        var core = await NewExchangeCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Sell, 3, 7001));
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Buy, 5));   // 交易所模式可吃边界 = 全簿（无庄家报价界）

        var daveTrades = trades.Snapshot.Where(t => t.AccountId == "dave").Select(t => t.Trade).ToList();
        Assert.Equal(2, daveTrades.Count);
        Assert.Equal(7000, daveTrades[0].Price, 6);   // 价低档先
        Assert.Equal(2, daveTrades[0].Volume);
        Assert.Equal(7001, daveTrades[1].Price, 6);
        Assert.Equal(3, daveTrades[1].Volume);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("dave", null)).OrderStatusRaw);
    }

    [Fact]
    public async Task FOK簿量不足整单作废()
    {
        var core = await NewExchangeCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 3, 7000));
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 5, timeCondition: CtpTimeCondition.FOK));   // 簿只有 3 手

        // FOK 不能全成 ⇒ 整单作废（无庄家兜底可补量）
        Assert.Equal(SimOrderStatus.NoTradeNotQueueing, Assert.Single(core.QueryOrders("bob", null)).OrderStatusRaw);
        Assert.Empty(trades.Snapshot.Where(t => t.AccountId == "bob"));
        // alice 的挂单原封不动
        Assert.Equal(SimOrderStatus.NoTradeQueueing, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);
    }

    [Fact]
    public async Task 市价残量作废不挂单()
    {
        var core = await NewExchangeCoreAsync();

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 2, 7000));
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 5));   // 簿只有 2 手——残量 3 手作废（不挂单）

        var bobOrder = Assert.Single(core.QueryOrders("bob", null));
        Assert.Equal(SimOrderStatus.PartTradedNotQueueing, bobOrder.OrderStatusRaw);   // '2' 部分成交不在队列
        Assert.Equal(2, bobOrder.VolumeTraded);
        Assert.Equal(0, bobOrder.VolumeTotal);   // 残量作废 ⇒ 剩余清零（与重启作废路径同款语义）
        // 残量不入簿：买侧仍空
        Assert.Equal(0, core.Market.GetQuote(SimHarness.Instrument)!.Value.BidPrice, 6);
    }

    [Fact]
    public async Task 订阅即推当前快照tick()
    {
        var core = await NewExchangeCoreAsync();

        // 订阅前簿里已有挂单（挂单时的 tick 没人听）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 5, 7000));

        var ticks = new Collector<CtpTick>();
        core.TickProduced += ticks.Add;
        var ok = core.ActivateMarket(SimHarness.Instrument);   // 订阅 ⇒ 立即推快照

        Assert.True(ok);
        var snapshot = Assert.Single(ticks.Snapshot.Where(t => t.InstrumentId == SimHarness.Instrument));
        Assert.Equal(7000, snapshot.BidPrice, 6);
        Assert.Equal(5, snapshot.BidVolume);
    }

    [Fact]
    public async Task 持仓盈亏按最新成交价()
    {
        var core = await NewExchangeCoreAsync();

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 1, 7000));
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Buy, 1));       // bob 多头 @7000
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Sell, 1, 7010));
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Buy, 1));      // 最新价推到 7010

        // bob 持仓浮盈 = (7010 − 7000) × 15 × 1 = 150——盯市价是**最新成交价**（不是游走值）
        Assert.Equal(150, core.QueryAccount("bob").PositionProfit, 6);
    }

    [Fact]
    public async Task 订阅即收到盘口快照_wire级路由()
    {
        // 回归（2026-09-28 实测踩中）：交易所模式空闲市场的唯一一帧 = 订阅激活时的盘口快照。
        // 曾因「先激活后登记订阅」被路由吞掉 ⇒ 订阅者板恒空（挂单明明在簿里）。
        // 修法 = 先登记订阅再激活——快照落进本 op 的 capture-during-dispatch 缓冲，应答帧后送达。
        // 做市商以外部客户端身份接入（2026-09-28 起 bots 即 Lean 策略）——此处经 core 直下等价模拟其挂单。
        using var sim = LoopbackSim.Start(o => o with { PasswordIterations = 1000 }, seedMaker: false);
        // 参与者双边挂单（任意普通账户——直接经 core 下单，服务端视角与外部客户端经 wire 下单等价）
        sim.Core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Buy, 5, 6988));
        sim.Core.InsertOrder("mm", SimHarness.Limit(CtpDirection.Sell, 5, 6992));

        await sim.Session.LoginAsync(new CtpLoginRequest
        {
            FrontAddress = "sim",
            MdFrontAddress = "",
            BrokerId = SimHarness.Broker,
            UserId = SimHarness.Account,
            Password = SimHarness.TestPassword,
            AppId = "aether",
            AuthCode = "auth",
        });

        var ticks = new Collector<CtpTick>();
        sim.Session.TickReceived += (_, t) => ticks.Add(t);

        await sim.Session.SubscribeMarketDataAsync(new CtpMarketDataConnect
        {
            MdFrontAddress = "tcp://127.0.0.1:40002",
            BrokerId = SimHarness.Broker,
            UserId = SimHarness.Account,
            Password = SimHarness.TestPassword,
        }, [SimHarness.Instrument]);

        // 订阅完成后必须立即见到做市双边盘口（无定时器续命、无后续市场事件——全靠激活快照）
        Assert.True(
            await Wait.UntilAsync(() => sim.Session.GetLatestTick(SimHarness.Instrument) is { BidPrice: > 0, AskPrice: > 0 }),
            "订阅后必须立即收到盘口快照");
        var tick = sim.Session.GetLatestTick(SimHarness.Instrument)!;
        Assert.Equal(6988, tick.BidPrice, 6);
        Assert.Equal(6992, tick.AskPrice, 6);
        Assert.Equal(5, tick.BidVolume);
        Assert.Equal(5, tick.AskVolume);
    }
}
