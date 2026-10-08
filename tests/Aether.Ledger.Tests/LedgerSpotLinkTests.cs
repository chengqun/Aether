using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Aether.Ledger.Tests;

/// <summary>
/// 批 4 接线验收（「充币 → 铸造现货持仓」的最后一焊）：<see cref="LedgerSpotLink"/> 把链侧 IOU
/// 与交易所核心的铸造台账/持仓对齐——**全栈真回路**（真链节点回环 + 真桥 + 真核心 + 真接线类，
/// 本测试正是当年两侧各自交付、中间没焊时漏掉的那一层）。
/// <para>对账模型：链是目标真相（IOU）、DB 是现值真相（铸造台账）、每轮只动差额；
/// 崩溃窗口（烧毁后链未确认）由下一轮对账补铸自愈。</para>
/// </summary>
public class LedgerSpotLinkTests
{
    private const string Instrument = "ax001";
    private static readonly DateTime SpotNow = new(2026, 9, 23, 10, 0, 0);   // 周三 10:00（交易时段）

    private sealed record Stack(BridgeLoopback Sim, SimTradingCore Core, LedgerSpotLink Link) : IDisposable
    {
        public void Dispose()
        {
            try { Link.Dispose(); } catch { }
            try { Sim.Dispose(); } catch { }
            try { Core.Dispose(); } catch { }
        }
    }

    /// <summary>起全套：链回环 + 纯内存核心（BOARD 上 ax001）+ 接线（对账周期拉长——测试用 ReconcileNow 确定性驱动）。</summary>
    private static async Task<Stack> StartAsync(string label, string[]? board = null)
    {
        var sim = await BridgeLoopback.StartAsync($"spot-{label}");
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = board ?? new[] { Instrument }, InitialBalance = 1_000_000 },   // buyer 用现金买币
            new SimClock(() => SpotNow));
        await core.InitializeAsync();
        var link = new LedgerSpotLink(core, sim.Bridge, sim.Client, Instrument,
            reconcileInterval: TimeSpan.FromSeconds(30));   // timer 兜底不扰断言
        sim.Bridge.WithdrawHook = link;
        await link.StartAsync();
        return new Stack(sim, core, link);
    }

    private static int LongVolume(SimTradingCore core, string account)
        => core.QueryPositions(account, Instrument).Where(p => p.PosiDirection == CtpPosiDirection.Long)
            .Sum(p => p.TotalPosition);

    private static CtpOrderRequest Limit(CtpDirection direction, int volume, double price,
        CtpOffsetFlag offset = CtpOffsetFlag.Open)
        => new()
        {
            BrokerId = "SIM",
            InvestorId = "",
            InstrumentId = Instrument,
            Direction = direction,
            OffsetFlag = offset,
            PriceType = CtpOrderPriceType.Limit,
            LimitPrice = price,
            Volume = volume,
            TimeCondition = CtpTimeCondition.GFD,
        };

    [Fact]
    public async Task 充币自动补铸_提币闭环_供给上限同步()
    {
        using var stack = await StartAsync("roundtrip");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("spot-roundtrip");

        // agent 先拿 3 币，充值到 custody（memo = u1——归账键）
        await LedgerBridge.DepositAsync(sim.Client, treasury, agent.Address, 3_000_000_000UL, "seed");
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, agent, sim.Custody.Address, 3_000_000_000UL, "u1");
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        // 补铸：u1 持仓 30 手（1 手 = 1 币 = 1e8 atoms）、台账 30、供给上限 30（custody 链上 3 币）
        Assert.Equal(30, LongVolume(stack.Core, "u1"));
        Assert.Equal(30, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);
        Assert.Equal(30, stack.Core.GetTotalLongVolume(Instrument));
        Assert.Equal(30, stack.Core.TryGetInstrument(Instrument)!.SupplyLimitLots);

        // 提币 1 币：烧持仓 1 手（先烧后签）→ custody 出金 1e8
        await sim.Bridge.WithdrawAsync("u1", agent.Address, 100_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(29, LongVolume(stack.Core, "u1"));                       // 烧毁
        Assert.Equal(29, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);  // 台账同步
        Assert.Equal(29, stack.Core.GetTotalLongVolume(Instrument));
        Assert.Equal(2_900_000_000UL, await sim.Bridge.GetCoinBalanceAsync("u1"));   // IOU 同步
        Assert.Equal(29, stack.Core.TryGetInstrument(Instrument)!.SupplyLimitLots);  // custody 余额 → 上限

        // 再对账一轮：两侧一致 ⇒ 幂等无动作（总 OI 不变）
        stack.Link.ReconcileNow();
        Assert.Equal(29, stack.Core.GetTotalLongVolume(Instrument));
    }

    [Fact]
    public async Task 买入后提币_IOU为零照提_不补铸()
    {
        // venue 持仓闸门的核心语义：buyer 的币是**买的**（IOU=0、台账=0）不是充的——持仓>0 照提；
        // 提走后对账不补铸（无铸造义务——币是从卖家转移来的，卖家的 IOU/台账不变）。
        using var stack = await StartAsync("bought");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("spot-bought");

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 200_000_000UL, "u1");
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();
        Assert.Equal(2, LongVolume(stack.Core, "u1"));

        // u1 挂卖 2 手 @100（充币铸造的持仓）；buyer 现金买入（全额保证金 200——现金换币）
        stack.Core.InsertOrder("u1", Limit(CtpDirection.Sell, 2, 100, CtpOffsetFlag.Close));
        stack.Core.InsertOrder("buyer", Limit(CtpDirection.Buy, 2, 100));
        Assert.Equal(2, LongVolume(stack.Core, "buyer"));

        // buyer 提币 2 币（IOU=0）：钩子烧持仓 2 手（台账 0−2 → clamp 0）→ custody 出金 2e8
        await sim.Bridge.WithdrawAsync("buyer", agent.Address, 200_000_000UL);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(0UL, await sim.Bridge.GetCoinBalanceAsync("buyer"));       // clamp（不为负不 underflow）
        Assert.Equal(0, LongVolume(stack.Core, "buyer"));                        // 币已离所
        Assert.Equal(0, stack.Core.GetSpotMintedVolumes(Instrument).TryGetValue("buyer", out var m) ? m : 0);
        // 不补铸：u1 的铸造义务不动（2 币充值过的账），总 OI = 0（币全数离所）
        Assert.Equal(2, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);
        Assert.Equal(0, stack.Core.GetTotalLongVolume(Instrument));
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(0UL, custody.BalanceAtoms);   // 2 币全数出金
    }

    [Fact]
    public async Task 烧毁后链未确认_对账补铸自愈()
    {
        // 崩溃窗口：烧毁成功、链上提交未发生（进程死了）——币没走 ⇒ 下一轮对账把持仓补铸回来。
        using var stack = await StartAsync("heal");
        var sim = stack.Sim;
        var agent = LedgerHarness.Wallet("spot-heal");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 200_000_000UL, "u1");
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();
        Assert.Equal(2, LongVolume(stack.Core, "u1"));

        // 直接调钩子（= 模拟 WithdrawAsync 在「烧毁后、SubmitTx 前」崩溃——链上无出金、IOU 不动）
        var (ok, error) = await stack.Link.TryBurnForWithdrawalAsync("u1", 100_000_000UL);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1, LongVolume(stack.Core, "u1"));   // 烧掉 1 手

        stack.Link.ReconcileNow();   // IOU 仍 2 币（链没走）→ 台账 1 → 差 +1 → 补铸

        Assert.Equal(2, LongVolume(stack.Core, "u1"));
        Assert.Equal(2, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);
        Assert.Equal(2, stack.Core.GetTotalLongVolume(Instrument));   // 总 OI 回到 = custody 余额（2 币）
    }

    [Fact]
    public async Task 粉尘不足一手_不铸造_对账稳定()
    {
        using var stack = await StartAsync("dust");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 150_000_000UL, "u1");
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        // 1.5 币 → 铸 1 手（floor）；粉尘 0.5 币留在链上 custody（不铸造不循环）
        Assert.Equal(1, LongVolume(stack.Core, "u1"));
        Assert.Equal(1, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);

        stack.Link.ReconcileNow();   // 再来一轮：目标 floor(1.5)=1 == 台账 1 ⇒ 幂等无动作
        stack.Link.ReconcileNow();
        Assert.Equal(1, stack.Core.GetTotalLongVolume(Instrument));
    }

    [Fact]
    public async Task 现货未上币_桥待命_提币拒绝()
    {
        // BOARD 只有 zx001（无现货）——对账跳过不报错（待命），提币钩子诚实拒绝。
        using var stack = await StartAsync("nolist", board: new[] { "zx001" });
        var agent = LedgerHarness.Wallet("spot-nolist");

        stack.Link.ReconcileNow();   // 无现货 ⇒ 待命（不抛）

        var (ok, error) = await stack.Link.TryBurnForWithdrawalAsync("u1", 100_000_000UL);
        Assert.False(ok);
        Assert.Contains("未上市", error);
    }

    /// <summary>等出块（高度达到 target）。</summary>
    private static async Task WaitForBlockAsync(BridgeLoopback sim, ulong targetHeight, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var info = await sim.Client.GetInfoAsync();
            if (info.Height >= targetHeight) return;
            await Task.Delay(100);
        }
        Assert.Fail($"等出块超时（期望高度 {targetHeight}）");
    }

    /// <summary>等桥 poll 追上（bridge 500ms 轮询）。</summary>
    private static async Task WaitForPollAsync(BridgeLoopback sim, int timeoutMs = 3000)
        => await Task.Delay(700);
}
