using System;
using System.Linq;
using System.Threading.Tasks;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 报单生命周期：状态级联（<c>'3'</c> 未成交排队 → <c>'1'</c> 部分成交 → <c>'0'</c> 全部成交）、
/// 有效期类型的尾部（GFD 留挂 / IOC·FOK·市价残量作废转 <c>'2'</c>/<c>'4'</c>）、撤单转 <c>'5'</c>，
/// 以及**外发顺序**与「每笔状态变化恰好一次」。
/// 流动性来自 maker 账户双边挂单（<see cref="SimHarness.SeedMaker"/>——价格发现归簿）。
/// </summary>
/// <remarks>
/// 核心（<see cref="SimTradingCore"/>）在调用线程上同步外发事件——异步性是 <c>SimCtpSession</c> 的泵加的，
/// 故本文件的断言可以直接跟在 <c>InsertOrder</c> 之后，无需轮询等待。
/// 回报收集按账户过滤（maker 的回报不得混入被测账户的时间线）。
/// </remarks>
public class OrderLifecycleTests
{
    private static async Task<(SimTradingCore Core, Collector<CtpOrder> Orders, Collector<CtpTrade> Trades)> SubscribedAsync(
        SimOptions? options = null, bool seedMaker = true)
    {
        var core = await SimHarness.NewLoggedInCoreAsync(options, seedMaker);
        var orders = new Collector<CtpOrder>();
        var trades = new Collector<CtpTrade>();
        core.OrderChanged += (a, o) => { if (a == SimHarness.Account) orders.Add(o); };
        core.TradeFilled += (a, t) => { if (a == SimHarness.Account) trades.Add(t); };
        return (core, orders, trades);
    }

    private static char Status(CtpOrder order) => (char)order.OrderStatusRaw;

    private static string Statuses(Collector<CtpOrder> orders) => new(orders.Snapshot.Select(Status).ToArray());

    [Fact]
    public async Task 市价单先报未成交再报全部成交()
    {
        var (core, orders, trades) = await SubscribedAsync();

        var inserted = core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));

        Assert.Equal("000000000001", inserted.OrderRef);

        // 真柜台的次序是「先 OnRtnOrder(未成交) 再 OnRtnOrder(全部成交) + OnRtnTrade」——
        // 若只推终态，客户端的订单列表会跳过中间态，依赖状态机过渡的策略会漏事件。
        Assert.Equal("30", Statuses(orders));
        Assert.All(orders.Snapshot, o => Assert.Equal(inserted.OrderRef, o.OrderRef));

        var trade = Assert.Single(trades.Snapshot);
        Assert.Equal(inserted.OrderRef, trade.OrderRef);
        Assert.Equal(SimHarness.Ask, trade.Price, 6); // 买成交在对手侧（maker 卖一）
        Assert.Equal(1, trade.Volume);
        Assert.Equal("1", trade.TradeId);
    }

    [Fact]
    public async Task 成交回报晚于其报单回报()
    {
        // 用一条共用时间线记录两类事件的相对次序（客户端按 OrderRef 配对，成交先到会配不上）
        var timeline = new System.Collections.Generic.List<string>();
        var core = await SimHarness.NewLoggedInCoreAsync();
        core.OrderChanged += (a, o) => { if (a == SimHarness.Account) lock (timeline) timeline.Add($"order:{Status(o)}"); };
        core.TradeFilled += (a, t) => { if (a == SimHarness.Account) lock (timeline) timeline.Add($"trade:{t.TradeId}"); };

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 2));

        Assert.Equal(new[] { "order:3", "order:0", "trade:1" }, timeline);
    }

    [Fact]
    public async Task 每个状态变化恰好外发一次()
    {
        var (core, orders, trades) = await SubscribedAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
        var immediateOrders = orders.Count;
        var immediateTrades = trades.Count;

        // 静默期后再数一遍——重复回调（旧缺陷）在这里会露出来
        Assert.Equal(immediateOrders, await Wait.QuiesceAsync(() => orders.Count));
        Assert.Equal(immediateTrades, await Wait.QuiesceAsync(() => trades.Count));
        Assert.Equal(2, immediateOrders);
        Assert.Equal(1, immediateTrades);

        // 同一单号的状态序列不应出现重复项相邻
        Assert.DoesNotContain(orders.Snapshot.Skip(1).Zip(orders.Snapshot, (now, prev) => now.OrderRef == prev.OrderRef && now.OrderStatusRaw == prev.OrderStatusRaw), x => x);
    }

    [Fact]
    public async Task GFD限价残量静息入簿并等对手方补足()
    {
        // 部分成交的另一半路径：GFD 限价残量**入簿**（不冻结释放），对手方后续插反向单即补足
        var (core, orders, trades) = await SubscribedAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, SimHarness.Ask));

        var inserted = core.InsertOrder(SimHarness.Account,
            SimHarness.Limit(CtpDirection.Buy, 3, SimHarness.Ask));   // 穿价吃 1，残 2 静息入簿
        Assert.Equal("31", Statuses(orders));

        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 2, SimHarness.Ask));   // 对手方补量 ⇒ 吃掉残 2
        Assert.Equal("310", Statuses(orders));

        Assert.Equal(2, trades.Count);
        Assert.Equal(new[] { "1", "2" }, trades.Snapshot.Select(t => t.TradeId).ToArray());

        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(0, account.FrozenMargin, 2); // 终态不留冻结
        Assert.Equal(0, account.FrozenCash, 2);
        Assert.Equal(3, core.QueryPositions(SimHarness.Account).Sum(p => p.TotalPosition));

        var order = Assert.Single(core.QueryOrders(SimHarness.Account));
        Assert.Equal(inserted.OrderRef, order.OrderRef);
        Assert.Equal(3, order.VolumeTraded);
    }

    [Fact]
    public async Task IOC未成交部分转部分成交不排队并退款()
    {
        // maker 卖侧只挂 1 手 ⇒ IOC 买 3 只吃到 1 手，残量作废（不挂簿）
        var (core, orders, trades) = await SubscribedAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, SimHarness.Ask));

        core.InsertOrder(SimHarness.Account,
            SimHarness.Market(CtpDirection.Buy, 3, timeCondition: CtpTimeCondition.IOC));

        // '2' = 部分成交、不再排队——已是终态，故尾部只推一次（FinalizeNoQueue 不再重复外发）
        Assert.Equal("32", Statuses(orders));
        Assert.Equal(1, trades.Count);

        var order = Assert.Single(core.QueryOrders(SimHarness.Account));
        Assert.Equal(0, order.VolumeTotal); // CTP 语义：不再排队 ⇒ 剩余量归零

        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12601.8, account.CurrMargin, 2);
        Assert.Equal(0, account.FrozenMargin, 2); // 未成交部分的冻结必须全额退回
        Assert.Equal(0, account.FrozenCash, 2);
        Assert.Equal(5.25, account.Commission, 2);
    }

    [Fact]
    public async Task FOK成交量不足则整单不成交并全额退款()
    {
        // 簿上只有 1 手 ⇒ FOK 买 3 整单作废（干跑先于落账——免回滚）
        var (core, orders, trades) = await SubscribedAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, SimHarness.Ask));

        core.InsertOrder(SimHarness.Account,
            SimHarness.Market(CtpDirection.Buy, 3, timeCondition: CtpTimeCondition.FOK));

        Assert.Equal("34", Statuses(orders)); // '4' = 全部未成交、不排队
        Assert.Empty(trades.Snapshot);

        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(0, account.CurrMargin, 2);
        Assert.Equal(0, account.FrozenMargin, 2);
        Assert.Equal(0, account.FrozenCash, 2);
        Assert.Equal(0, account.Commission, 2);
        Assert.Equal(SimHarness.InitialBalance, account.Balance, 2); // 分文未动
        Assert.Empty(core.QueryPositions(SimHarness.Account));
    }

    [Fact]
    public async Task 限价单未穿过对手价时只挂不成交()
    {
        var (core, orders, trades) = await SubscribedAsync();

        // maker 卖一 7001 > 买限价 6900 ⇒ 不可成交
        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));

        Assert.Equal("3", Statuses(orders));
        Assert.Empty(trades.Snapshot);
        Assert.Equal(6900, orders.Snapshot[0].LimitPrice, 6);
        Assert.Equal(1, orders.Snapshot[0].VolumeTotal);

        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12420, account.FrozenMargin, 2); // 按委托价（非对手价）冻结
        Assert.True(account.FrozenCash > 0);
        Assert.Equal(0, account.CurrMargin, 2);
    }

    [Fact]
    public async Task 限价单穿过对手价时按对手价成交而非委托价()
    {
        var (core, orders, trades) = await SubscribedAsync();

        // 买限价 7005 ≥ maker 卖一 7001 ⇒ 成交，且成交价取挂单价（委托价只表示可接受上限）
        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 7005));

        Assert.Equal("30", Statuses(orders));
        var trade = Assert.Single(trades.Snapshot);
        Assert.Equal(SimHarness.Ask, trade.Price, 6);
    }

    [Fact]
    public async Task 撤单转已撤销并退回全部冻结()
    {
        var (core, orders, trades) = await SubscribedAsync();

        var inserted = core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 2, 6900));
        var before = core.QueryAccount(SimHarness.Account);
        Assert.Equal(2, before.FrozenMargin / 12420, 4); // 2 手 ⇒ 冻结恰为 1 手的两倍

        core.CancelOrder(SimHarness.Account, SimHarness.RefOf(orders.Snapshot[0]));

        Assert.Equal("35", Statuses(orders));
        Assert.Empty(trades.Snapshot);

        var after = core.QueryAccount(SimHarness.Account);
        Assert.Equal(0, after.FrozenMargin, 2);
        Assert.Equal(0, after.FrozenCash, 2);
        Assert.Equal(SimHarness.InitialBalance, after.Balance, 2); // 撤单不产生任何费用
        Assert.Equal(SimHarness.InitialBalance, after.Available, 2);

        var order = Assert.Single(core.QueryOrders(SimHarness.Account));
        Assert.Equal(inserted.OrderRef, order.OrderRef);
        Assert.Equal(0, order.VolumeTotal);
    }

    [Fact]
    public async Task 卖开与买平走通空头链路()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1));

        var short_ = Assert.Single(core.QueryPositions(SimHarness.Account));
        Assert.Equal(CtpPosiDirection.Short, short_.PosiDirection);
        Assert.Equal(1, short_.TotalPosition);

        // 平仓方向必须是「买」，且开平标志为 Close
        core.InsertOrder(SimHarness.Account,
            SimHarness.Market(CtpDirection.Buy, 1, CtpOffsetFlag.Close));

        Assert.Empty(core.QueryPositions(SimHarness.Account));

        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(0, account.CurrMargin, 2);
        // 卖开在买一 6999、买平在卖一 7001 ⇒ 空头亏 2 点 × 15
        Assert.Equal(-30, account.CloseProfit, 2);
    }

    [Fact]
    public async Task 平仓回报的开平标志与成交编号独立递增()
    {
        var (core, _, trades) = await SubscribedAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close));

        Assert.Equal(new[] { CtpOffsetFlag.Open, CtpOffsetFlag.Close },
            trades.Snapshot.Select(t => t.OffsetFlag).ToArray());
        Assert.Equal(new[] { "1", "2" }, trades.Snapshot.Select(t => t.TradeId).ToArray());

        // 单号连续分配，不因撤单/拒单而回退或重号
        Assert.Equal(new[] { "000000000001", "000000000002" },
            core.QueryOrders(SimHarness.Account).Select(o => o.OrderRef).ToArray());
    }

    [Fact]
    public async Task 已撤报单不再被后续市场事件触碰()
    {
        // 「已撤的单不再产生任何回报」：撤后打两笔对手方市场单（其它账户成交），被测账户零回报
        var (core, orders, _) = await SubscribedAsync();
        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));
        Assert.Equal("3", Statuses(orders));

        core.CancelOrder(SimHarness.Account, SimHarness.RefOf(orders.Snapshot[0]));
        Assert.Equal("35", Statuses(orders));

        core.InsertOrder("noise", SimHarness.Market(CtpDirection.Buy, 1));
        core.InsertOrder("noise", SimHarness.Market(CtpDirection.Buy, 1));
        Assert.Equal("35", Statuses(orders)); // 已撤单不再产生任何回报
    }
}
