using Acme.Ctp.Domain;
using Acme.Ctp.Simulator.Storage;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 落盘与重启续用：仿真柜台的价值一半在「今天跑完，明天接着跑」——
/// 而这条路径**没有任何编译期检查**（存储是 <see cref="ISimStore"/> 端口，
/// 核心对「有没有真落盘」无感知），只能靠真实的落盘 → 重开验证。
/// <para>
/// 另钉住两个**序列**：报单号与成交号。状态（账户/持仓/报单）恢复对了但序列没续上，
/// 症状是「重启后第一张单重号」与「重启后成交记录被静默覆盖」——两者都不报错。
/// </para>
/// </summary>
public class PersistenceTests
{
    /// <summary>临时目录下的仿真库路径（绝不碰用户真实的 ctp-sim.db）。</summary>
    private static string TempDbPath()
        => Path.Combine(Path.GetTempPath(), $"aether-ctp-sim-{Guid.NewGuid():N}", "ctp-sim.db");

    /// <summary>不自动喂行情（价格只由注入决定 ⇒ 撮合价可预期）+ 落盘到指定文件。</summary>
    private static SimOptions OptionsFor(string dbPath) => new() { DatabasePath = dbPath, TickIntervalMs = 0, MarketMode = SimMarketMode.Walk };

    private static async Task<SimTradingCore> OpenCoreAsync(string dbPath)
    {
        var core = new SimTradingCore(OptionsFor(dbPath), new SimClock(() => SimHarness.TradingNow));
        await core.InitializeAsync();
        core.InjectQuote(SimHarness.Instrument, SimHarness.Bid, SimHarness.Ask, SimHarness.Last);
        SimHarness.Login(core);
        return core;
    }

    [Fact]
    public async Task 仿真库落盘后重启账本可续用()
    {
        // 这条钉的是「host 重启（核心重建 + InitializeAsync 恢复）后账本续用」——独立 host 形态下
        // 落盘由 host 进程负责（LoopbackSim.Dispose = 停 listener + core.Dispose 同步 flush），
        // 重启 = 同一 db 再起一个 LoopbackSim。这条链路没有任何编译期检查，只能靠真实的落盘→重开验证。
        var dir = Path.Combine(Path.GetTempPath(), $"aether-ctp-sim-{Guid.NewGuid():N}");
        var dbPath = Path.Combine(dir, "ctp-sim.db");
        Directory.CreateDirectory(dir);
        try
        {
            // 用不自动喂行情的选项，价格只由注入决定 ⇒ 撮合价可预期
            var configure = (Func<SimOptions, SimOptions>)(o => o with { DatabasePath = dbPath, TickIntervalMs = 0 });

            using (var first = LoopbackSim.Start(configure))
            {
                await first.Session.LoginAsync(new CtpLoginRequest
                {
                    FrontAddress = "tcp://127.0.0.1:40001",
                    MdFrontAddress = "tcp://127.0.0.1:40002",
                    AppId = "aether",
                    AuthCode = "auth",
                    BrokerId = SimHarness.Broker,
                    UserId = SimHarness.Account,
                    Password = "任意",
                });

                await first.Session.OrderInsertAsync(SimHarness.Market(CtpDirection.Buy, 2));
                Assert.Single(await first.Session.QueryOrderAsync());

                Assert.True(File.Exists(dbPath), $"仿真库应落在指定路径：{dbPath}");
            }   // Dispose：session 断开 → server 停 → core.Dispose（同步 flush）——确定性落盘点

            using (var second = LoopbackSim.Start(configure))
            {
                await second.Session.LoginAsync(new CtpLoginRequest
                {
                    FrontAddress = "tcp://127.0.0.1:40001",
                    MdFrontAddress = "tcp://127.0.0.1:40002",
                    AppId = "aether",
                    AuthCode = "auth",
                    BrokerId = SimHarness.Broker,
                    UserId = SimHarness.Account,
                    Password = "任意",
                });

                // 同一账号的报单与持仓跨会话仍在（单号也从上次的序号续排，不重号）
                Assert.Single(await second.Session.QueryOrderAsync());
                Assert.Equal(2, (await second.Session.QueryPositionAsync()).Single().TotalPosition);

                var next = await second.Session.OrderInsertAsync(SimHarness.Market(CtpDirection.Buy, 1));
                Assert.NotEqual("000000000001", next.OrderRef);
            }
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 重启后报单号从上次的序号续排()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using (var first = await OpenCoreAsync(dbPath))
            {
                // 挂单不成交（限价低于买一）——目的是**占据**两个单号
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6901));
                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                // 单号必须续上：重号会让 Accept 覆盖历史单，且客户端侧单号重复
                var next = second.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6902));

                Assert.Equal("000000000003", next.OrderRef);
                Assert.Equal(3, second.QueryOrders(SimHarness.Account).Count);

                // 单号互异是硬约束（历史 2 笔 + 新的 1 笔）
                var refs = second.QueryOrders(SimHarness.Account).Select(o => o.OrderRef).ToList();
                Assert.Equal(3, refs.Distinct(StringComparer.Ordinal).Count());
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 重启后未成交挂单作废且冻结额清零()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using (var first = await OpenCoreAsync(dbPath))
            {
                first.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));
                var before = first.QueryAccount(SimHarness.Account);
                Assert.True(before.FrozenMargin > 0, "挂单应冻结保证金");

                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                // 报单本身留着（历史可见），但状态作废、残量归零
                var order = Assert.Single(second.QueryOrders(SimHarness.Account));
                Assert.Equal((char)SimOrderStatus.NoTradeNotQueueing, (char)order.OrderStatusRaw);
                Assert.Equal(0, order.VolumeTotal);
                Assert.Contains("作废", order.StatusMsg);

                // 冻结额必须清零——否则重启后可用资金凭空少一块（挂单已不在，钱得有去处）
                var after = second.QueryAccount(SimHarness.Account);
                Assert.Equal(0, after.FrozenMargin, 2);
                Assert.Equal(0, after.FrozenCash, 2);
                Assert.Equal(after.Balance, after.Available, 2);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 重启后成交号续排不覆盖上次成交()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            string firstTradeId;
            using (var first = await OpenCoreAsync(dbPath))
            {
                var trades = new Collector<CtpTrade>();
                first.TradeFilled += (_, t) => trades.Add(t);

                first.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
                firstTradeId = Assert.Single(trades.Snapshot).TradeId;

                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                var trades = new Collector<CtpTrade>();
                second.TradeFilled += (_, t) => trades.Add(t);

                second.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
                var secondTradeId = Assert.Single(trades.Snapshot).TradeId;

                // 成交号从 1 重新开始会静默覆盖上次的成交行（sim_trade 主键含 trade_id）
                Assert.NotEqual(firstTradeId, secondTradeId);
                await second.FlushAsync();
            }

            // 两条成交都还在（没被覆盖）
            var ids = await SimStore.ForFile(dbPath).LoadTradeIdsAsync(SimHarness.Account);
            Assert.Equal(2, ids.Count);
            Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 合约表播种幂等跨重启不重复()
    {
        var dbPath = TempDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            int seedCount;
            using (var first = await OpenCoreAsync(dbPath))
            {
                var instruments = first.QueryInstruments();
                seedCount = instruments.Count;
                Assert.True(seedCount >= 9, $"内置合约表应已播种，实际 {seedCount}");
                Assert.Single(instruments, i => i.InstrumentId == SimHarness.Instrument);
                await first.FlushAsync();
            }

            using (var second = await OpenCoreAsync(dbPath))
            {
                var instruments = second.QueryInstruments();
                Assert.Equal(seedCount, instruments.Count);
                Assert.Single(instruments, i => i.InstrumentId == SimHarness.Instrument);

                // 元数据也从库里回来了（不是重新播种的副本）
                var ag = instruments.Single(i => i.InstrumentId == SimHarness.Instrument);
                Assert.Equal(SimHarness.VolumeMultiple, ag.VolumeMultiple, 6);
                Assert.Equal(SimHarness.MarginRate, ag.MarginRate, 6);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }
    }

    [Fact]
    public async Task 不配数据库路径时零磁盘且重启即失忆()
    {
        // SimOptions 默认不落盘（单测与「纯内存仿真」走这条路）——默认值一旦被改成有路径，
        // 每个跑过仿真的用户目录下都会凭空多一个 db 文件，故把默认值本身钉住。
        Assert.Null(new SimOptions().DatabasePath);

        var dbPath = TempDbPath();
        try
        {
            // DatabasePath 为 null ⇒ 核心内部取 NullSimStore：跑完一整套交易也不该有任何文件产生
            var core = new SimTradingCore(
                new SimOptions { TickIntervalMs = 0, DatabasePath = null, MarketMode = SimMarketMode.Walk },
                new SimClock(() => SimHarness.TradingNow));
            await core.InitializeAsync();
            core.InjectQuote(SimHarness.Instrument, SimHarness.Bid, SimHarness.Ask, SimHarness.Last);
            SimHarness.Login(core);

            core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));

            await core.FlushAsync();   // 空操作，不该抛
            Assert.Single(core.QueryOrders(SimHarness.Account));
            core.Dispose();

            Assert.False(File.Exists(dbPath), "未配 DatabasePath 却写出了文件");
            Assert.False(Directory.Exists(Path.GetDirectoryName(dbPath)!));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(dbPath)!)) Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        }

        // NullSimStore 自身的契约：读恒空、写恒成功（核心靠它做到「无存储也能跑」）
        var nullStore = NullSimStore.Instance;
        await nullStore.InitializeAsync();
        Assert.Empty(await nullStore.LoadAccountsAsync());
        Assert.Empty(await nullStore.LoadOrdersAsync(SimHarness.Account));
        Assert.Empty(await nullStore.LoadTradeIdsAsync(SimHarness.Account));
        await nullStore.SaveAccountAsync(new SimAccountSnapshot(SimHarness.Account, 1, 0, 0, 0, 0, 0, 0));
        Assert.Empty(await nullStore.LoadAccountsAsync());
    }
}
