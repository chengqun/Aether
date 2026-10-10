using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Aether.Ledger.Tests;

/// <summary>
/// 计价币桥接线验收（批 4/6 → **2026-10-10 币通道退役后 AQ 单资产**）：<see cref="LedgerSpotLink"/>
/// 把链侧 AQ IOU 与交易所核心的现金台账对齐——**全栈真回路**（真链节点回环 + 真桥 + 真核心 + 真接线类）。
/// <para>对账模型：链是目标真相（AQ IOU）、DB 是现值真相（sim_quote_minted 台账）、每轮只动差额；
/// 崩溃窗口（扣现后链未确认）由下一轮对账补回自愈。coin 不进交易所（充值跳过不归账）。</para>
/// </summary>
public class LedgerSpotLinkTests
{
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

    /// <summary>起全套：链回环 + 纯内存核心（BOARD 上 ax001）+ 接线（对账周期拉长——测试用 ReconcileNow 确定性驱动）。
    /// initialBalance=0（零水龙头——现金只从 AQ 充值来）。</summary>
    private static async Task<Stack> StartAsync(string label, double initialBalance = 0)
    {
        var sim = await BridgeLoopback.StartAsync($"spot-{label}");
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { "ax001" }, InitialBalance = initialBalance },
            new SimClock(() => SpotNow));
        await core.InitializeAsync();
        var link = new LedgerSpotLink(core, sim.Bridge, reconcileInterval: TimeSpan.FromSeconds(30));   // timer 兜底不扰断言
        sim.Bridge.WithdrawHook = link;
        await link.StartAsync();
        return new Stack(sim, core, link);
    }

    private const string Instrument = "ax001";

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
    public async Task 充AQ得现金_提现扣现金_双向闭环()
    {
        using var stack = await StartAsync("roundtrip");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("spot-roundtrip");

        // treasury 直接充 AQ 到 custody（memo = u1——归账键）
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            1_000_000_000UL, "u1", LedgerSupply.Assets.Quote);
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        // 现金入账：10 AQ = 10 现金（1 AQ = 1 现金）
        Assert.Equal(10, stack.Core.QueryAccount("u1").Balance, 2);
        Assert.Equal(1_000_000_000UL, await sim.Bridge.GetQuoteBalanceAsync("u1"));   // AQ IOU 同步（原子数）

        // 提现 3 AQ（=3e8 atoms）：先扣场内现金（钩子）→ custody 签名出金 → 对账扣台账
        await sim.Bridge.WithdrawAsync("u1", agent.Address, 300_000_000UL);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(7, stack.Core.QueryAccount("u1").Balance, 2);                     // 现金扣减
        Assert.Equal(700_000_000UL, await sim.Bridge.GetQuoteBalanceAsync("u1"));
        var agentChain = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(300_000_000UL, agentChain.QuoteBalanceAtoms);                     // 链上到账

        // 再对账一轮：两侧一致 ⇒ 幂等无动作
        stack.Link.ReconcileNow();
        Assert.Equal(7, stack.Core.QueryAccount("u1").Balance, 2);
    }

    [Fact]
    public async Task 扣现后链未确认_对账补回自愈()
    {
        // 崩溃窗口：钩子扣现金成功、链上提交未发生（进程死了）——AQ 没走 ⇒ 下一轮对账把现金补回来。
        using var stack = await StartAsync("heal");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            200_000_000UL, "u1", LedgerSupply.Assets.Quote);   // 2 AQ
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();
        Assert.Equal(2, stack.Core.QueryAccount("u1").Balance, 2);

        // 直接调钩子（= 模拟 WithdrawAsync 在「扣现后、SubmitTx 前」崩溃——链上无出金、IOU 不动）
        var (ok, error) = await stack.Link.TryBurnForWithdrawalAsync("u1", 100_000_000UL);
        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(1, stack.Core.QueryAccount("u1").Balance, 2);   // 扣掉 1 AQ

        stack.Link.ReconcileNow();   // IOU 仍 2 AQ（链没走）→ 台账 1 → 差 +1 → 补回现金

        Assert.Equal(2, stack.Core.QueryAccount("u1").Balance, 2);   // 自愈——钱没走，现金回来
    }

    [Fact]
    public async Task coin充值跳过不归账_现金通道不受影响()
    {
        // 币通道退役钉子：链上误转 coin 到 custody——桥跳过不归账；AQ 通道照常。
        using var stack = await StartAsync("coin-skip");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            100_000_000UL, "u1", LedgerSupply.Assets.Quote);   // 1 AQ
        await WaitForBlockAsync(sim, 1);
        var info = await sim.Client.GetInfoAsync();
        await sim.Client.SubmitTxAsync(LedgerTransaction.Sign(info.ChainId, treasury, sim.Custody.Address,
            2_000_000_000UL, 2, "u2", LedgerSupply.Assets.Coin));   // 2 coin 误充（memo=u2）
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(1, stack.Core.QueryAccount("u1").Balance, 2);   // AQ 正常入账
        Assert.Equal(0, stack.Core.QueryAccount("u2").Balance, 2);   // coin 跳过——u2 现金 0
    }

    [Fact]
    public async Task 小数AQ精确入账_对账稳定()
    {
        // AQ 8 位小数的原子粒度：1.5 AQ → 台账记 1.5e8 原子（与 IOU 原子级一致）、现金精确入账 1.5
        //（CreditQuoteDeposit 的 double 除法保留小数——无粉尘丢失）。
        using var stack = await StartAsync("dust");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));

        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            150_000_000UL, "u1", LedgerSupply.Assets.Quote);   // 1.5 AQ
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(1.5, stack.Core.QueryAccount("u1").Balance, 2);   // 精确入账（无取整丢失）

        stack.Link.ReconcileNow();   // 再来两轮：台账原子数 == IOU 原子数 ⇒ 幂等无动作
        stack.Link.ReconcileNow();
        Assert.Equal(1.5, stack.Core.QueryAccount("u1").Balance, 2);
    }

    [Fact]
    public async Task AQ入金_双向开仓成交_提现回自持_零水龙头闭环()
    {
        // 用户场景旗舰钉子（AQ 单资产版）：零水龙头（InitialBalance=0）下市场完整转起来——
        // 现金全部来自 AQ 充值；ax001 双向开仓（10% 保证金）撮合成交；提现 AQ 回自持钱包。
        using var stack = await StartAsync("binance");
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("binance-alice");

        // 死锁现状钉子：新账户零现金
        stack.Core.Login("SIM", "u1", "pw");
        Assert.Equal(0, stack.Core.QueryAccount("u1").Balance, 2);

        // 1) 充 AQ：u1 2000（卖方手续费+保证金）、u2 10000（买方）
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            200_000_000_000UL, "u1", LedgerSupply.Assets.Quote);
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            1_000_000_000_000UL, "u2", LedgerSupply.Assets.Quote);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        Assert.Equal(2000, stack.Core.QueryAccount("u1").Balance, 2);
        Assert.Equal(10_000, stack.Core.QueryAccount("u2").Balance, 2);

        // 2) 双向开仓成交：u1 挂卖开空 5 手 @100（10% 保证金 = 50 现金）；u2 买开 5 手吃单
        stack.Core.InsertOrder("u1", Limit(CtpDirection.Sell, 5, 100));
        stack.Core.InsertOrder("u2", Limit(CtpDirection.Buy, 5, 100));
        Assert.Equal(5, stack.Core.QueryPositions("u2", Instrument)
            .Where(p => p.PosiDirection == CtpPosiDirection.Long).Sum(p => p.TotalPosition));
        Assert.Equal(5, stack.Core.QueryPositions("u1", Instrument)
            .Where(p => p.PosiDirection == CtpPosiDirection.Short).Sum(p => p.TotalPosition));

        // 3) 提现 100 AQ 回 alice 钱包（先扣场内可用现金 → custody 出金）
        await sim.Bridge.WithdrawAsync("u1", alice.Address, 10_000_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);
        stack.Link.ReconcileNow();

        // 现金 = 2000 − 100（提现）− 0.1（maker 费：挂卖 5 手 @100 被 taker 吃，2bp × 500）= 1899.9
        var u1Final = stack.Core.QueryAccount("u1");
        Assert.Equal(1899.9, u1Final.Balance - u1Final.PositionProfit, 2);
        var aliceChain = await sim.Client.GetBalanceAsync(alice.Address.Hex);
        Assert.Equal(10_000_000_000UL, aliceChain.QuoteBalanceAtoms);   // 100 AQ 回自持
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
