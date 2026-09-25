using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 随机游走的**可复现性**与推进顺序：同种子必须逐 tick 同价，否则「跑了两小时的策略复现不出问题」。
/// </summary>
/// <remarks>
/// <para>
/// 三条确定性设计在这里被逐一钉死：
/// </para>
/// <list type="number">
/// <item><b>每合约独立 <see cref="Random"/></b>，种子 = <c>Seed ^ 合约名稳定哈希</c>（FNV-1a，非
/// <c>string.GetHashCode</c>——后者每进程随机化，会直接破坏跨进程可复现）。</item>
/// <item><b>ordinal 排序推进</b>（<c>Advance</c> 按合约名序推进）——字典枚举序不保证稳定。</item>
/// <item><b>订阅即激活</b>：<c>Advance</c> 只推进已存在 walk 的合约，而 walk 由
/// <c>GetQuote</c>/<c>Inject</c>/<c>Activate</c> 首次访问时创建。</item>
/// </list>
/// <para>
/// 契合设计文档的「历史 tick 回放」诉求：固定 <see cref="SimOptions.Seed"/> 即可让同一份回放脚本
/// 每次跑出完全一样的行情与撮合结果。
/// </para>
/// </remarks>
public class DeterminismTests
{
    private static SimOptions Seeded(int seed, int maxWalkTicks = 3) => new()
    {
        TickIntervalMs = 0,
        DatabasePath = null,
        Seed = seed,
        MaxWalkTicks = maxWalkTicks,
    };

    /// <summary>推进 <paramref name="steps"/> 次并收集该合约的最新价序列。</summary>
    private static double[] Walk(SimTradingCore core, string instrument, int steps)
    {
        var prices = new List<double> { core.Market.GetQuote(instrument)!.Value.LastPrice };
        for (var i = 0; i < steps; i++)
        {
            core.Market.Advance();
            prices.Add(core.Market.GetQuote(instrument)!.Value.LastPrice);
        }
        return prices.ToArray();
    }

    // —— 种子 ——

    [Fact]
    public async Task 同种子两次独立运行得到逐tick相同的价格序列()
    {
        var a = await SimHarness.NewCoreAsync(Seeded(4242));
        var b = await SimHarness.NewCoreAsync(Seeded(4242));

        Assert.Equal(Walk(a, SimHarness.Instrument, 30), Walk(b, SimHarness.Instrument, 30));
    }

    [Fact]
    public async Task 不同种子产生不同的价格序列()
    {
        var a = await SimHarness.NewCoreAsync(Seeded(4242));
        var b = await SimHarness.NewCoreAsync(Seeded(4243));

        // 30 步后完全撞上的概率极低；若真撞上说明种子没有真正参与 Random 构造
        Assert.NotEqual(Walk(a, SimHarness.Instrument, 30), Walk(b, SimHarness.Instrument, 30));
    }

    [Fact]
    public async Task 同种子下不同合约的游走互不相同()
    {
        // 若种子只按 Seed 构造（漏掉合约名哈希），全部合约会走出同一条曲线——实盘语义上荒谬
        var core = await SimHarness.NewCoreAsync(Seeded(4242));
        core.ActivateMarket("rb2610");
        core.InjectQuote("rb2610", 3197, 3199, 3198);

        var ag = Walk(core, SimHarness.Instrument, 20);

        // rb 与 ag 的步数序列应当分叉（这里只比较「相对起点」的偏移，两者起点价不同）
        var rb = Walk(core, "rb2610", 20).Select(p => p - 3198).ToArray();
        var agOffset = ag.Select(p => p - ag[0]).ToArray();

        Assert.NotEqual(agOffset, rb);
    }

    [Fact]
    public async Task 注入报价后游走从注入点续走而非回到中枢()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242));

        core.InjectQuote(SimHarness.Instrument, 7099, 7101, 7100);
        var before = core.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice;
        Assert.Equal(7100, before, 6);

        core.Market.Advance();
        var after = core.Market.GetQuote(SimHarness.Instrument)!.Value.LastPrice;

        // 走一步最多 3 个 tick，绝无可能瞬间回到 7000 的中枢
        Assert.True(Math.Abs(after - 7100) <= 3, $"注入后应从注入点续走，实际从 {before} 跳到 {after}");
    }

    // —— 步长与涨跌停板 ——

    [Fact]
    public async Task 步长上限与最大游走tick一致()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242, maxWalkTicks: 1));
        var prices = Walk(core, SimHarness.Instrument, 40);

        // MaxWalkTicks = 1 且起点远离涨跌停板 ⇒ 每步恰好移动 1 个 tick（步长 1、tick 1）
        for (var i = 1; i < prices.Length; i++)
            Assert.Equal(1, Math.Abs(prices[i] - prices[i - 1]), 6);
    }

    [Fact]
    public async Task 长跑后价格仍夹在涨跌停板内()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242));
        var instrument = core.QueryInstruments().Single(i => i.InstrumentId == SimHarness.Instrument);

        var upper = instrument.RoundToTick(instrument.PreSettlement * (1 + instrument.LimitRate));
        var lower = instrument.RoundToTick(instrument.PreSettlement * (1 - instrument.LimitRate));

        foreach (var price in Walk(core, SimHarness.Instrument, 500))
        {
            Assert.InRange(price, lower, upper);
        }

        // 500 步 × 最多 3 tick 足以撞到板边，故上界断言不是空跑
        Assert.True(instrument.PreSettlement * instrument.LimitRate > 0);
    }

    [Fact]
    public async Task 价格始终对齐最小变动价位()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242));

        foreach (var price in Walk(core, SimHarness.Instrument, 60))
        {
            // ag2610 的 tick = 1 ⇒ 价格必须是整数（RoundToTick 的 6 位小数舍入后无小数部分）
            Assert.Equal(Math.Round(price), price, 6);
        }
    }

    // —— 推进顺序与激活 ——

    [Fact]
    public async Task 推进结果按合约名ordinal排序()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242));

        // 只用**种子表内**的合约：激活路径不做按品种派生（见下一个用例），未登记的代码注入即被静默忽略
        Assert.True(core.Market.Activate("IF2610"));
        core.InjectQuote("IF2610", 3899, 3901, 3900);
        Assert.True(core.Market.Activate("rb2610"));
        core.InjectQuote("rb2610", 3197, 3199, 3198);

        var advanced = core.Market.Advance();

        // ordinal 升序：'I'(0x49) < 'a'(0x61) < 'r' ⇒ IF2610 < ag2610 < rb2610
        // （字典枚举序不保证稳定，故 Advance 必须显式排序）
        Assert.Equal(new[] { "IF2610", "ag2610", "rb2610" }, advanced.Select(q => q.InstrumentId).ToArray());
    }

    [Fact]
    public async Task 未被激活的合约不参与推进()
    {
        var core = await SimHarness.NewCoreAsync(Seeded(4242));

        // 只推进一次：此时唯一的 walk 是夹具注入过行情的 ag2610
        var advanced = core.Market.Advance();
        Assert.Equal(new[] { SimHarness.Instrument }, advanced.Select(q => q.InstrumentId).ToArray());

        // 激活一个不曾被碰过的合约后才出现在推进结果里
        Assert.True(core.Market.Activate("cu2610"));
        Assert.Contains("cu2610", core.Market.Advance().Select(q => q.InstrumentId));
    }

    [Fact]
    public async Task 合约表外的合约要先经下单登记才进得了行情引擎()
    {
        var core = await SimHarness.NewLoggedInCoreAsync(Seeded(4242));

        // 行情引擎只认**已登记**的合约——EnsureWalk 是纯字典查找，不做按品种派生，
        // 故未登记的代码 Activate 失败、GetQuote 为 null，注入/推进都会被静默忽略
        Assert.False(core.Market.Activate("no-such-variety-xyz"));
        Assert.Null(core.Market.GetQuote("no-such-variety-xyz"));
        Assert.Null(core.Market.GetQuote("ag2602"));

        // 登记只发生在**下单路径**（ResolveInstrument → DeriveByVariety）：ag2602 不在种子表但 ag 品种认识
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1, instrument: "ag2602"));
        Assert.Contains("ag2602", core.QueryInstruments().Select(i => i.InstrumentId));

        // 登记之后行情引擎才认它（这条次序是「随便填个合约也能跑通流程」的前提）
        Assert.True(core.Market.Activate("ag2602"));
        Assert.NotNull(core.Market.GetQuote("ag2602"));
    }

    // —— 合成 tick 外发开关 ——

    [Fact]
    public void 仅在开启合成行情时外发tick()
    {
        var off = NewEngine(new SimOptions { ExposeSyntheticTicks = false });
        var offTicks = new List<CtpTick>();
        off.TickGenerated += (_, t) => offTicks.Add(t);
        off.Inject("ag2610", 6999, 7001, 7000);
        off.Advance();
        Assert.Empty(offTicks); // Phase 1 默认：合成 tick 只喂撮合，不外发（Md 走真实前置）

        var on = NewEngine(new SimOptions { ExposeSyntheticTicks = true });
        var onTicks = new List<CtpTick>();
        on.TickGenerated += (_, t) => onTicks.Add(t);
        on.Inject("ag2610", 6999, 7001, 7000);
        Assert.Single(onTicks); // Inject 本身也外发

        on.Advance();
        Assert.Equal(2, onTicks.Count);

        // 注入的那一帧：数值直接来自注入参数（未经过游走）
        var injected = onTicks[0];
        Assert.Equal("ag2610", injected.InstrumentId);
        Assert.Equal("SHFE", injected.ExchangeId);
        Assert.Equal(7001, injected.AskPrice, 6);
        Assert.Equal(6999, injected.BidPrice, 6);
        Assert.Equal(6990, injected.PreSettlement, 6); // ag2610 的昨结算
        Assert.Equal("20260923", injected.TradingDay); // 夹具时钟 2026-09-23 10:00 ⇒ 日盘交易日即当日

        // 推进出的一帧是游走产物：价格本身随种子走，故只钉**不变量**——盘口形状恒为中枢 ∓1 tick
        var advanced = onTicks[1];
        Assert.Equal(2, advanced.AskPrice - advanced.BidPrice, 6); // 价差恒为 2 个最小变动价位（PriceTick = 1）
        Assert.Equal(injected.PreSettlement, advanced.PreSettlement, 6);
        Assert.Equal(injected.TradingDay, advanced.TradingDay);
    }

    [Fact]
    public void 合成tick的累计成交量单调不减()
    {
        var engine = NewEngine(new SimOptions { ExposeSyntheticTicks = true, Seed = 4242 });
        var ticks = new List<CtpTick>();
        engine.TickGenerated += (_, t) => ticks.Add(t);

        engine.Activate("ag2610");
        for (var i = 0; i < 10; i++) engine.Advance();

        Assert.Equal(10, ticks.Count);
        for (var i = 1; i < ticks.Count; i++)
            Assert.True(ticks[i].Volume >= ticks[i - 1].Volume, "累计成交量不得回退");
    }

    [Fact]
    public void 时段闸门关闭时闭市也照常推进()
    {
        // 2026-09-26 03:00 是周六凌晨（闭市）——GateByTradingTime 默认 false ⇒ 照常游走
        var lenient = NewEngine(new SimOptions { Seed = 4242 }, new DateTime(2026, 9, 26, 3, 0, 0));
        lenient.Activate("ag2610");
        var before = lenient.GetQuote("ag2610")!.Value.LastPrice;
        lenient.Advance();
        Assert.NotEqual(before, lenient.GetQuote("ag2610")!.Value.LastPrice);

        // 开启闸门：同一时刻价格原地不动（仿真默认不挑时段，闸门是 opt-in）
        var strict = NewEngine(
            new SimOptions { Seed = 4242, GateByTradingTime = true }, new DateTime(2026, 9, 26, 3, 0, 0));
        strict.Activate("ag2610");
        var frozen = strict.GetQuote("ag2610")!.Value.LastPrice;
        strict.Advance();
        Assert.Equal(frozen, strict.GetQuote("ag2610")!.Value.LastPrice, 6);
    }

    /// <summary>独立构造一个行情引擎（不经过核心）——用于只关心 tick 外发的用例。</summary>
    private static SimMarketDataEngine NewEngine(SimOptions options, DateTime? now = null)
    {
        var known = InstrumentCatalog.Seed().ToDictionary(i => i.InstrumentId, StringComparer.OrdinalIgnoreCase);
        return new SimMarketDataEngine(
            options,
            new SimClock(() => now ?? SimHarness.TradingNow),
            id => known.TryGetValue(id, out var i) ? i : null);
    }
}
