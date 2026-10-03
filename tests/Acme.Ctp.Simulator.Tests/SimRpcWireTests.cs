using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Domain;
using Acme.Ctp.Sim;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 传输层（<see cref="SimRpcClient"/> ↔ SimRpcServer）专项测试——SessionAdapterTests 钉**接缝契约**，
/// 本文件钉**线上行为**：帧相关、跨连接隔离、顺序保证（应答先于推送——Lean CtpOrderTracker 依赖）、
/// host 未启动的错误形态。
/// </summary>
public class SimRpcWireTests
{
    private static CtpOrderRequest Market(CtpDirection direction, int volume)
        => SimHarness.Market(direction, volume);

    private static async Task<SimRpcClient> LoginClientAsync(LoopbackSim sim, string userId = SimHarness.Account)
    {
        var client = new SimRpcClient("127.0.0.1", sim.Server.LocalEndpoint!.Port);
        await client.ConnectAsync();
        await client.LoginAsync(new LoginArg(SimHarness.Broker, userId, SimHarness.TestPassword));
        return client;
    }

    [Fact]
    public async Task 各操作经裸客户端往返保持语义()
    {
        using var sim = LoopbackSim.Start();
        using var client = await LoginClientAsync(sim);

        // hello
        var hello = client.Hello!;
        Assert.Equal(1, hello.ProtocolVersion);

        // 查询空账本
        var account = await client.QueryAccountAsync();
        Assert.Equal(SimHarness.Account, account.AccountId);
        Assert.Equal(SimHarness.InitialBalance, account.Balance, 2);
        Assert.Empty(await client.QueryPositionsAsync(null));
        Assert.Empty(await client.QueryOrdersAsync(null));

        // 下单（fire-and-forget 语义：应答只带 OrderRef/RequestId）
        var inserted = await client.InsertOrderAsync(Market(CtpDirection.Buy, 1));
        Assert.Equal("000000000001", inserted.OrderRef);
        Assert.Equal(1, inserted.RequestId);

        // 查询反映成交（服务端在应答帧前已完成撮合）
        var orders = await client.QueryOrdersAsync(null);
        var order = Assert.Single(orders);
        Assert.Equal(1, order.VolumeTraded);
        Assert.Single(await client.QueryPositionsAsync(SimHarness.Instrument));

        // 撤单定位失败 → 业务异常（ErrorId 原样跨线）
        var bogus = new CtpOrderRef(SimHarness.Broker, SimHarness.Account, 1, 1, "999999999999", SimHarness.Exchange);
        var ex = await Assert.ThrowsAsync<CtpBusinessException>(() => client.CancelOrderAsync(bogus));
        Assert.Equal(SimErrorCodes.OrderNotFound, ex.ErrorId);

        // 订阅（host 恒推送——本例无后续市场事件，只验往返不抛）
        await client.SubscribeAsync([SimHarness.Instrument]);
    }

    [Fact]
    public async Task 应答先于本单推送到达()
    {
        // F1 顺序保证：朴素直写会让推送先于应答（核心在 InsertOrder 内同步抛事件）——
        // Lean 的 CtpOrderTracker 在 OrderInsertAsync 应答后才注册 OrderRef，先到的成交会被静默丢弃。
        using var sim = LoopbackSim.Start();
        using var client = await LoginClientAsync(sim);

        var orderPushedAfterResponse = 0;
        var responseObserved = false;
        client.OrderPushed += _ =>
        {
            // 推送到达时若应答尚未完成 ⇒ 顺序违约
            if (!responseObserved) throw new InvalidOperationException("推送先于应答到达（F1 顺序违约）");
            Interlocked.Increment(ref orderPushedAfterResponse);
        };

        var inserted = await client.InsertOrderAsync(Market(CtpDirection.Buy, 1));
        responseObserved = true;   // await 返回 ⇒ 应答已完成（推送事件先于此行则违约）

        Assert.True(await Wait.UntilAsync(() => orderPushedAfterResponse >= 2), "市价单应有两个订单回报推送");
        Assert.Equal("000000000001", inserted.OrderRef);
    }

    [Fact]
    public async Task 并发下单无串扰且单号互异()
    {
        using var sim = LoopbackSim.Start();
        using var client = await LoginClientAsync(sim);

        // 10 笔并发 insert 经同一连接流水线化——id 相关表不得张冠李戴
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.InsertOrderAsync(Market(CtpDirection.Buy, 1))));

        var refs = results.Select(r => r.OrderRef).ToList();
        Assert.Equal(10, refs.Distinct(StringComparer.Ordinal).Count());   // 单号互异（串扰 ⇒ 重号）
        Assert.All(results, r => Assert.True(r.RequestId >= 1));

        var orders = await client.QueryOrdersAsync(null);
        Assert.Equal(10, orders.Count);
    }

    [Fact]
    public async Task host未启动时报错带启动命令()
    {
        using var sim = LoopbackSim.Start();
        var deadPort = sim.Server.LocalEndpoint!.Port;
        sim.Dispose();   // 释放端点 ⇒ 该端口不再监听

        var client = new SimRpcClient("127.0.0.1", deadPort);
        using var _ = client;

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(() => client.ConnectAsync());
        Assert.Equal(SimWireErrors.NotRunning, ex.ErrorId);
        Assert.Contains("Acme.Ctp.Sim.Host", ex.ErrorMsg);   // 提示里有启动命令，用户照抄即能救
    }

    [Fact]
    public async Task 双连接按账户隔离推送()
    {
        using var sim = LoopbackSim.Start();
        using var alice = await LoginClientAsync(sim, "alice");
        using var bob = await LoginClientAsync(sim, "bob");

        var aliceOrders = new Collector<CtpOrder>();
        var bobOrders = new Collector<CtpOrder>();
        alice.OrderPushed += o => aliceOrders.Add(o);
        bob.OrderPushed += o => bobOrders.Add(o);

        await alice.InsertOrderAsync(Market(CtpDirection.Buy, 1));
        Assert.True(await Wait.UntilAsync(() => aliceOrders.Count >= 2));
        Assert.Equal(0, await Wait.QuiesceAsync(() => bobOrders.Count));   // B 一条也收不到

        // 两账户账本独立
        var aliceAccount = await alice.QueryAccountAsync();
        var bobAccount = await bob.QueryAccountAsync();
        Assert.Equal("alice", aliceAccount.AccountId);
        Assert.Equal("bob", bobAccount.AccountId);
        Assert.True(aliceAccount.CurrMargin > 0);
        Assert.Equal(0, bobAccount.CurrMargin, 2);
    }

    [Fact]
    public async Task 同账户多连接广播推送()
    {
        // 已知限制（CLAUDE.md）：同账户双客户端会从同一 MaxOrderRef 起各自编号 ⇒ 撞号。
        // 本条只钉「广播」这一半：两个连接都收到自己账户的推送。
        using var sim = LoopbackSim.Start();
        using var first = await LoginClientAsync(sim);
        using var second = await LoginClientAsync(sim);

        var firstOrders = new Collector<CtpOrder>();
        var secondOrders = new Collector<CtpOrder>();
        first.OrderPushed += o => firstOrders.Add(o);
        second.OrderPushed += o => secondOrders.Add(o);

        await first.InsertOrderAsync(Market(CtpDirection.Buy, 1));

        Assert.True(await Wait.UntilAsync(() => firstOrders.Count >= 2), "下单方必须收到推送");
        Assert.True(await Wait.UntilAsync(() => secondOrders.Count >= 2), "同账户另一连接也必须收到（广播）");
    }

    [Fact]
    public async Task tick按订阅路由到对应连接()
    {
        // 行情随柜台（恒推送）的 wire 级 pin：无需任何开关，订阅即激活 + 按订阅集路由
        // （rb2610 须上架才能激活——激活即推零值快照；ag2610 的快照来自 maker 双边挂单）
        using var sim = LoopbackSim.Start(o => o with
        {
            ListedInstruments = new[] { SimHarness.Instrument, "rb2610" },
        });
        using var a = await LoginClientAsync(sim, "alice");
        using var b = await LoginClientAsync(sim, "bob");

        var aTicks = new Collector<CtpTick>();
        var bTicks = new Collector<CtpTick>();
        a.TickPushed += t => aTicks.Add(t);
        b.TickPushed += t => bTicks.Add(t);

        await a.SubscribeAsync([SimHarness.Instrument]);          // alice 只订 ag2610
        await b.SubscribeAsync(["rb2610"]);                       // bob 只订 rb2610

        Assert.True(await Wait.UntilAsync(() => aTicks.Count > 0), "alice 须收到 ag2610 行情");
        Assert.True(await Wait.UntilAsync(() => bTicks.Count > 0), "bob 须收到 rb2610 行情");

        // 各收各的：路由按订阅集过滤
        Assert.All(aTicks.Snapshot, t => Assert.Equal(SimHarness.Instrument, t.InstrumentId));
        Assert.All(bTicks.Snapshot, t => Assert.Equal("rb2610", t.InstrumentId));
    }

    [Fact]
    public async Task queryInstruments默认上市表往返()
    {
        // 默认上市表 = zx001（须显式把 ListedInstruments 归 null——SimHarness 默认上架 ag2610 供交易用例）
        using var sim = LoopbackSim.Start(o => o with { ListedInstruments = null }, seedMaker: false);
        using var client = await LoginClientAsync(sim);

        var list = await client.QueryInstrumentsAsync();

        var sim001 = Assert.Single(list);   // 默认 = zx001（本交易所自有品种——官方命名合规 2 字母+3 数字）
        Assert.Equal("zx001", sim001.InstrumentId);
        Assert.Equal("SIM", sim001.ExchangeId);
        Assert.Equal(3798, sim001.PreSettlement, 6);
    }

    [Fact]
    public async Task queryInstruments自定义上市表()
    {
        using var sim = LoopbackSim.Start(o => o with { ListedInstruments = new[] { "ag2610", "xx2601" } }, seedMaker: false);
        using var client = await LoginClientAsync(sim);

        var list = await client.QueryInstrumentsAsync();

        Assert.Equal(2, list.Count);
        Assert.Contains(list, i => i.InstrumentId == "xx2601" && i.BasePrice == 100);   // 未知品种合成兜底
        Assert.Contains(list, i => i.InstrumentId == "ag2610" && i.ExchangeId == "SHFE");
    }
}
