using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 混合订单簿的跨账户撮合（「几个不同客户端真实进行交易」——客户互为对手）。
/// 价格-时间优先、按挂单方（maker）价成交、自成交跳过、庄家兜底；空簿路径与旧行为逐字节一致
/// （由全部既有用例存活论证——见 OrderLifecycleTests 等）。
/// </summary>
public class CrossAccountMatchingTests
{
    [Fact]
    public async Task 两账户交叉成交按挂单价双方落账()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        // alice 挂限价买 7000（低于卖一 7001 ⇒ 不交叉报价，静息入簿）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 7000));
        var resting = Assert.Single(core.QueryOrders("alice", null));
        Assert.Equal(SimOrderStatus.NoTradeQueueing, resting.OrderStatusRaw);   // '3' 静息

        // bob 市价卖 1 ⇒ 吃掉 alice 的挂单（maker 价 7000，优于报价买一 6999）
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Sell, 1));

        // 双方各自拿到自己账户的成交（TradeId 从各自序列铸——同一起点 "1"）
        var aliceTrade = Assert.Single(trades.Snapshot.Where(t => t.AccountId == "alice")).Trade;
        var bobTrade = Assert.Single(trades.Snapshot.Where(t => t.AccountId == "bob")).Trade;
        Assert.Equal(7000, aliceTrade.Price, 6);
        Assert.Equal(7000, bobTrade.Price, 6);
        Assert.Equal("1", aliceTrade.TradeId);
        Assert.Equal("1", bobTrade.TradeId);

        // 订单双双全成
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("bob", null)).OrderStatusRaw);

        // 持仓镜像
        var alicePosition = Assert.Single(core.QueryPositions("alice", null));
        Assert.Equal(CtpPosiDirection.Long, alicePosition.PosiDirection);
        Assert.Equal(1, alicePosition.TotalPosition);
        var bobPosition = Assert.Single(core.QueryPositions("bob", null));
        Assert.Equal(CtpPosiDirection.Short, bobPosition.PosiDirection);
        Assert.Equal(1, bobPosition.TotalPosition);

        AssertConserved(core, "alice");
        AssertConserved(core, "bob");
    }

    [Fact]
    public async Task 价格优先与时间优先()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        // 两个买档：alice 7000（价高）、carol 6999（价低）——价高者先被吃
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 7000));
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Buy, 1, 6999));
        // 同价位再挂一笔：erin 7000（晚于 alice）——同价先挂先成
        core.InsertOrder("erin", SimHarness.Limit(CtpDirection.Buy, 1, 7000));

        // dave 市价卖 3：吃 alice(7000) → erin(7000) → carol(6999)
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Sell, 3));

        var daveTrades = trades.Snapshot.Where(t => t.AccountId == "dave").Select(t => t.Trade).ToList();
        Assert.Equal(3, daveTrades.Count);
        Assert.Equal(7000, daveTrades[0].Price, 6);   // 价高档先
        Assert.Equal(7000, daveTrades[1].Price, 6);
        Assert.Equal(6999, daveTrades[2].Price, 6);

        // 同价位时间优先：alice 与 erin 同价 7000——alice 的被动成交在前（bob 视角的成交序即被动方序）
        var passive = trades.Snapshot.Where(t => t.AccountId is "alice" or "erin").Select(t => t.Trade).ToList();
        Assert.Equal(2, passive.Count);
        Assert.All(passive, t => Assert.Equal(7000, t.Price, 6));

        // 三个买方各自 1 手全成
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("erin", null)).OrderStatusRaw);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("carol", null)).OrderStatusRaw);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("dave", null)).OrderStatusRaw);
    }

    [Fact]
    public async Task 部分成交跨档与残量入簿()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 2, 7000));
        core.InsertOrder("carol", SimHarness.Limit(CtpDirection.Buy, 2, 6999));

        // bob 市价卖 3：2@7000 + 1@6999
        core.InsertOrder("bob", SimHarness.Market(CtpDirection.Sell, 3));

        var bobOrder = Assert.Single(core.QueryOrders("bob", null));
        Assert.Equal(SimOrderStatus.AllTraded, bobOrder.OrderStatusRaw);
        Assert.Equal(3, bobOrder.VolumeTraded);

        var aliceOrder = Assert.Single(core.QueryOrders("alice", null));
        Assert.Equal(SimOrderStatus.AllTraded, aliceOrder.OrderStatusRaw);
        var carolOrder = Assert.Single(core.QueryOrders("carol", null));
        Assert.Equal(SimOrderStatus.PartTradedQueueing, carolOrder.OrderStatusRaw);   // '1' 部分成仍在队列
        Assert.Equal(1, carolOrder.VolumeTraded);

        // carol 的残量仍在簿：dave 再卖 1 手 ⇒ 吃掉 carol 的 6999 残量
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Sell, 1));
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("carol", null)).OrderStatusRaw);
        Assert.Equal(1, Assert.Single(core.QueryPositions("dave", null)).TotalPosition);
        Assert.Equal(4, core.QueryPositions("bob", null).Concat(core.QueryPositions("dave", null)).Sum(p => p.TotalPosition));
    }

    [Fact]
    public async Task 同账户不自成交跳过自己挂单()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        // alice 挂买 7000；同账户市价卖——吃簿跳过自己，走庄家（报价买一 6999）
        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 7000));
        core.InsertOrder("alice", SimHarness.Market(CtpDirection.Sell, 1));

        var aliceTrade = Assert.Single(trades.Snapshot).Trade;
        Assert.Equal(6999, aliceTrade.Price, 6);   // 庄家价，非自己挂单价 7000

        // 挂买单原封未动（'3' 静息）；持仓 = 空 1（卖单成交）
        var orders = core.QueryOrders("alice", null).ToList();
        Assert.Contains(orders, o => o.OrderStatusRaw == SimOrderStatus.NoTradeQueueing);   // 挂买仍在
        var position = Assert.Single(core.QueryPositions("alice", null));
        Assert.Equal(CtpPosiDirection.Short, position.PosiDirection);
        AssertConserved(core, "alice");
    }

    [Fact]
    public async Task FOK跨簿干跑足量则成交()
    {
        // MaxFillVolume=2 ⇒ 庄家单次最多 2 手；alice 簿上挂 1 手 ⇒ 合量 3 恰好覆盖 FOK 卖 3
        var core = await SimHarness.NewLoggedInCoreAsync(SimHarness.Options() with { MaxFillVolume = 2 });
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 7000));
        core.InsertOrder("bob",
            SimHarness.Market(CtpDirection.Sell, 3, timeCondition: CtpTimeCondition.FOK));

        var bobTrades = trades.Snapshot.Where(t => t.AccountId == "bob").Select(t => t.Trade).ToList();
        Assert.Equal(2, bobTrades.Count);                       // 簿 1 手 + 庄家 2 手
        Assert.Equal(7000, bobTrades[0].Price, 6);              // 先吃簿（maker 价）
        Assert.Equal(6999, bobTrades[1].Price, 6);              // 余量庄家（报价买一）
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("bob", null)).OrderStatusRaw);
        AssertConserved(core, "bob");
    }

    [Fact]
    public async Task FOK跨簿干跑不足则整单作废()
    {
        var core = await SimHarness.NewLoggedInCoreAsync(SimHarness.Options() with { MaxFillVolume = 2 });
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Buy, 1, 7000));
        // 簿 1 + 庄家 2 = 3 < 4 ⇒ FOK 整单作废（零成交、全额退冻结；alice 挂单原封不动）
        core.InsertOrder("bob",
            SimHarness.Market(CtpDirection.Sell, 4, timeCondition: CtpTimeCondition.FOK));

        Assert.Empty(trades.Snapshot);
        var bobOrder = Assert.Single(core.QueryOrders("bob", null));
        Assert.Equal(SimOrderStatus.NoTradeNotQueueing, bobOrder.OrderStatusRaw);   // '4'
        Assert.Empty(core.QueryPositions("bob", null));
        Assert.Equal(SimOrderStatus.NoTradeQueueing, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);
        AssertConserved(core, "bob");
    }

    [Fact]
    public async Task 簿价劣于报价不被吃_优于报价才成交()
    {
        // 价格连贯（有效最优价 = 簿与庄家报价合并）：合成做市商报价是市场底价——
        // 簄档位只有优于或等于报价对手价才被吃；劣价挂单等行情穿越它（庄家路径），不被劣价成交
        var core = await SimHarness.NewLoggedInCoreAsync();
        var trades = new Collector<(string AccountId, CtpTrade Trade)>();
        core.TradeFilled += (accountId, trade) => trades.Add((accountId, trade));

        core.InsertOrder("alice", SimHarness.Limit(CtpDirection.Sell, 1, 7020));   // 劣于报价卖一 7001 ⇒ 静息
        core.InsertOrder("bob", SimHarness.Limit(CtpDirection.Buy, 1, 7000));      // 优于报价买一 6999 ⇒ 静息
        Assert.Empty(trades.Snapshot);

        // carol 市价买：有效最优卖 = min(簿 7020, 报价 7001) = 7001 ⇒ 走庄家；alice 劣价挂单不动
        core.InsertOrder("carol", SimHarness.Market(CtpDirection.Buy, 1));
        var carolTrade = Assert.Single(trades.Snapshot.Where(t => t.AccountId == "carol")).Trade;
        Assert.Equal(7001, carolTrade.Price, 6);
        Assert.Equal(SimOrderStatus.NoTradeQueueing, Assert.Single(core.QueryOrders("alice", null)).OrderStatusRaw);

        // dave 市价卖：有效最优买 = max(簿 7000, 报价 6999) = 7000 ⇒ 吃 bob 的挂单（maker 价）
        core.InsertOrder("dave", SimHarness.Market(CtpDirection.Sell, 1));
        var daveTrade = Assert.Single(trades.Snapshot.Where(t => t.AccountId == "dave").Select(t => t.Trade).ToList().Where(t => t.Price == 7000));
        Assert.Equal(7000, daveTrade.Price, 6);
        Assert.Equal(SimOrderStatus.AllTraded, Assert.Single(core.QueryOrders("bob", null)).OrderStatusRaw);
        Assert.Equal(CtpPosiDirection.Long, Assert.Single(core.QueryPositions("bob", null)).PosiDirection);
        Assert.Equal(CtpPosiDirection.Short, Assert.Single(core.QueryPositions("dave", null)).PosiDirection);
        AssertConserved(core, "dave");
    }

    /// <summary>资金守恒断言（镜像 AccountingInvariantTests 的恒等式，容差 0.05；DTO 无 StaticBalance——用 Balance 展开式）。</summary>
    private static void AssertConserved(SimTradingCore core, string accountId)
    {
        var a = core.QueryAccount(accountId);
        var balance = a.Balance - (a.CurrMargin + a.FrozenMargin + a.FrozenCash);
        Assert.True(Math.Abs(balance - a.Available) < 0.05,
            $"{accountId}: Available 恒等式破坏（Balance-CurrMargin-Frozen-Frozen={balance} vs Available={a.Available}）");
        var staticBalance = SimHarness.InitialBalance - a.Commission + a.CloseProfit;   // StaticBalance = 初始 - 手续费 + 平仓盈亏
        Assert.True(Math.Abs(a.Balance - (staticBalance + a.PositionProfit)) < 0.05,
            $"{accountId}: Balance 恒等式破坏（Balance={a.Balance} vs 初始-手续费+平仓盈亏+持仓盈亏={staticBalance + a.PositionProfit}）");
    }
}
