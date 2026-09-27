using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 历史 tick 回放驱动（「我就是交易所」的价格源之二）：CSV 时间轴 → InjectQuote →
/// TickProduced/推送/撮合全复用。随机游走是价格源之一；（未来）真实行情转发之三。
/// </summary>
public class TickReplayTests
{
    [Fact]
    public async Task 回放按CSV时间轴注价()
    {
        var path = WriteReplayCsv("""
            time,instrument,bid,ask,last,volume
            09:00:00,sim001,3798,3800,3799,100
            09:00:01,sim001,3799,3801,3800,220
            09:00:03,sim001,3800,3802,3801,350
            """);
        try
        {
            var core = await SimHarness.NewCoreAsync();
            var ticks = new Collector<CtpTick>();
            core.TickProduced += t => ticks.Add(t);

            core.StartReplay(path, speed: 1000, loop: false);   // 行间 1s/2s ⇒ 1ms/2ms

            Assert.True(await Wait.UntilAsync(() => ticks.Count >= 3), "回放必须按行产出 tick");
            var snapshot = ticks.Snapshot.Take(3).ToList();
            Assert.Equal(3799, snapshot[0].LastPrice, 6);
            Assert.Equal(3800, snapshot[1].LastPrice, 6);
            Assert.Equal(3801, snapshot[2].LastPrice, 6);
            Assert.Equal(220, snapshot[1].Volume);              // 累计量回放保真
            Assert.All(snapshot, t => Assert.Equal("sim001", t.InstrumentId));

            var q = core.Market.GetQuote("sim001");
            Assert.Equal(3800, q!.Value.BidPrice, 6);           // 终态 = 最后一帧
            Assert.Equal(3802, q.Value.AskPrice, 6);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task 回放循环模式文件尾停顿后从头再来()
    {
        var path = WriteReplayCsv("""
            time,instrument,bid,ask,last
            09:00:00,sim001,3798,3800,3799
            09:00:01,sim001,3799,3801,3800
            """);
        try
        {
            var core = await SimHarness.NewCoreAsync();
            var ticks = new Collector<CtpTick>();
            core.TickProduced += t => ticks.Add(t);

            core.StartReplay(path, speed: 1000, loop: true);

            // 2 行/圈 + 尾停 1s ⇒ 等到 ≥4 帧即证明至少跑完第二圈
            Assert.True(await Wait.UntilAsync(() => ticks.Count >= 4), "循环模式必须从头再来");
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task 回放启动即停随机游走()
    {
        // TickIntervalMs=25 的随机 feed 本该持续产 tick——StartReplay 必须让它让位（否则游走在回放价上加噪）
        var path = WriteReplayCsv("""
            time,instrument,bid,ask,last
            09:00:00,sim001,3798,3800,3799
            09:00:01,sim001,3799,3801,3800
            """);
        try
        {
            var core = await SimHarness.NewCoreAsync(SimHarness.Options() with { TickIntervalMs = 25 });
            var ticks = new Collector<CtpTick>();
            core.TickProduced += t => ticks.Add(t);

            core.StartReplay(path, speed: 1000, loop: false);
            Assert.True(await Wait.UntilAsync(() => ticks.Count >= 2), "回放两行必须到达");

            await Task.Delay(200);   // 静置——随机 feed 若还活着，25ms 间隔早该刷出十几帧
            Assert.Equal(2, ticks.Count);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task 回放文件坏表头抛参数异常()
    {
        var path = WriteReplayCsv("not,a,header\n09:00:00,sim001,1,2,3\n");
        try
        {
            var core = await SimHarness.NewCoreAsync();
            Assert.Throws<ArgumentException>(() => core.StartReplay(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task 注入未登记合约自动解析入表()
    {
        var core = await SimHarness.NewCoreAsync();
        core.InjectQuote("sim777", 101, 103, 102);   // sim 品种派生（此前是静默 no-op 的坑）
        var q = core.Market.GetQuote("sim777");
        Assert.NotNull(q);
        Assert.Equal(102, q!.Value.LastPrice, 6);
        Assert.Contains("sim777", core.QueryInstruments().Select(i => i.InstrumentId));
    }

    private static string WriteReplayCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sim-replay-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
