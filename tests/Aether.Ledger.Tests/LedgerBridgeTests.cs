namespace Aether.Ledger.Tests;

/// <summary>桥测试夹具：节点回环 + custody 钱包 + 桥实例（共用 LedgerNodeLoopback 的节点）。</summary>
internal sealed class BridgeLoopback : IDisposable
{
    private readonly LedgerNodeLoopback _node;

    public LedgerClient Client => _node.Client;
    public LedgerWallet Custody { get; }
    public LedgerBridge Bridge { get; }
    public string ChainFile => _node.ChainFile;
    public string TreasurySeed => _node.TreasurySeed;

    private BridgeLoopback(LedgerNodeLoopback node, LedgerWallet custody, LedgerBridge bridge)
    {
        _node = node;
        Custody = custody;
        Bridge = bridge;
    }

    /// <summary>启动：节点回环（临时端口 + 新链）+ custody 钱包（固定种子确定性）+ 桥（StartAsync）。</summary>
    public static async Task<BridgeLoopback> StartAsync(string label)
    {
        var node = await LedgerNodeLoopback.StartNewAsync($"bridge-{label}");
        var custody = new LedgerWallet(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"custody-{label}")));
        var bridge = new LedgerBridge(node.Client, custody,
            msg => Console.WriteLine($"[BRIDGE-TEST] {msg}"));
        await bridge.StartAsync();
        return new BridgeLoopback(node, custody, bridge);
    }

    public void Dispose()
    {
        try { Bridge.Dispose(); } catch { }
        try { _node.Dispose(); } catch { }
    }
}

/// <summary>
/// 批 3 交易所桥验收（2026-10-10 币通道退役后：**只收付计价币 AQ**）：充值（agent 签名链上转账 →
/// memo 归账 AQ IOU）→ 提现（IOU 借记 + custody 签名出金）→ 双向闭环 + 链文件独立验证 +
/// 桥重启（链重扫恢复 IOU——不依赖任何独立持久化）。coin 的充提一律拒/跳过。
/// </summary>
public class LedgerBridgeTests
{
    private const uint Quote = LedgerSupply.Assets.Quote;

    [Fact]
    public async Task 充值到提现_双向闭环()
    {
        using var sim = await BridgeLoopback.StartAsync("roundtrip");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("agent-roundtrip");

        // treasury → agent（链上转账，让 agent 有 AQ 可充；coin 转账不影响 AQ nonce 流）
        await LedgerBridge.DepositAsync(sim.Client, treasury, agent.Address,
            10_000_000_000UL, "unused", asset: Quote);
        await WaitForBlockAsync(sim, 1);

        // agent 充 AQ 到交易所 custody（memo = "agent-account"——归账键）
        await LedgerBridge.DepositAsync(sim.Client, agent, sim.Custody.Address,
            3_000_000_000UL, "agent-account", asset: Quote);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        // AQ IOU 已入账
        var iou = await sim.Bridge.GetQuoteBalanceAsync("agent-account");
        Assert.Equal(3_000_000_000UL, iou);

        // 提现：IOU 借记 + custody 签名出金
        await sim.Bridge.WithdrawAsync("agent-account", agent.Address, 1_000_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);

        // IOU 已扣
        var afterWithdraw = await sim.Bridge.GetQuoteBalanceAsync("agent-account");
        Assert.Equal(2_000_000_000UL, afterWithdraw);

        // 链上 agent AQ 余额 = 原有 - 3（充值）+ 1（提现回）= 8
        var chainBalance = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(8_000_000_000UL, chainBalance.QuoteBalanceAtoms);

        // custody 链上 AQ 余额 = 3 - 1 = 2
        var custodyBalance = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(2_000_000_000UL, custodyBalance.QuoteBalanceAtoms);
    }

    [Fact]
    public async Task 提现余额不足_拒绝()
    {
        using var sim = await BridgeLoopback.StartAsync("insufficient");
        var agent = LedgerHarness.Wallet("agent-insufficient");

        // 未充值就提
        var ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => sim.Bridge.WithdrawAsync("nobody", agent.Address, 1_000_000UL));
        Assert.Contains("余额不足", ex.Message);
    }

    [Fact]
    public async Task 币通道已退役_coin充值提现一律拒()
    {
        using var sim = await BridgeLoopback.StartAsync("coin-retired");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("agent-coin-retired");

        // coin 充值便捷入口：直接拒
        var depositEx = await Assert.ThrowsAsync<LedgerClientException>(
            () => LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
                1_000_000_000UL, "coin-account", asset: LedgerSupply.Assets.Coin));
        Assert.Contains("币通道已退役", depositEx.Message);

        // coin 提现：直接拒
        var withdrawEx = await Assert.ThrowsAsync<LedgerClientException>(
            () => sim.Bridge.WithdrawAsync("coin-account", agent.Address, 1_000_000_000UL, LedgerSupply.Assets.Coin));
        Assert.Contains("币通道已退役", withdrawEx.Message);

        // 即使链上有人误转 coin 到 custody：桥跳过不归账（AQ IOU 不受影响）
        var info = await sim.Client.GetInfoAsync();
        var coinTx = LedgerTransaction.Sign(info.ChainId, treasury, sim.Custody.Address,
            2_000_000_000UL, 1, "mistaken-coin", LedgerSupply.Assets.Coin);
        await sim.Client.SubmitTxAsync(coinTx);
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);
        Assert.Equal(0UL, await sim.Bridge.GetQuoteBalanceAsync("mistaken-coin"));
    }

    [Fact]
    public async Task 桥重启_链重扫恢复IOU_不依赖独立持久化()
    {
        using var sim = await BridgeLoopback.StartAsync("restart");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("agent-restart");

        // 充 AQ
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            5_000_000_000UL, "restart-account", asset: Quote);
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);

        var iou = await sim.Bridge.GetQuoteBalanceAsync("restart-account");
        Assert.Equal(5_000_000_000UL, iou);

        // 提现一部分
        await sim.Bridge.WithdrawAsync("restart-account", agent.Address, 2_000_000_000UL);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        var afterWithdraw = await sim.Bridge.GetQuoteBalanceAsync("restart-account");
        Assert.Equal(3_000_000_000UL, afterWithdraw);

        // 「链是唯一真相」：独立验证器复核链文件（在 using 内——fixture Dispose 会删文件）。
        // 重启恢复语义 = StartAsync 的全量重扫从链重建 IOU（引擎重放确定性已覆盖）——
        // custody 链上 AQ 余额 = 5 - 2 = 3 = IOU（链上真相与桥内存态一致）。
        var verify = LedgerVerifier.VerifyFile(sim.ChainFile);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal(3_000_000_000UL, verify.Accounts[sim.Custody.Address.Hex].QuoteBalanceAtoms);
    }

    [Fact]
    public async Task 多账户充提_归账互不干扰()
    {
        using var sim = await BridgeLoopback.StartAsync("multi");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("bridge-alice");
        var bob = LedgerHarness.Wallet("bridge-bob");

        // treasury → alice/bob（链上先给 AQ——同钱包背靠背须等块推进 nonce）
        await LedgerBridge.DepositAsync(sim.Client, treasury, alice.Address, 4_000_000_000UL, "seed", asset: Quote);
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, treasury, bob.Address, 6_000_000_000UL, "seed", asset: Quote);
        await WaitForBlockAsync(sim, 2);

        // 两个账户各自充 AQ 到 custody
        await LedgerBridge.DepositAsync(sim.Client, alice, sim.Custody.Address, 1_000_000_000UL, "alice-account", asset: Quote);
        await WaitForBlockAsync(sim, 3);
        await LedgerBridge.DepositAsync(sim.Client, bob, sim.Custody.Address, 2_000_000_000UL, "bob-account", asset: Quote);
        await WaitForBlockAsync(sim, 4);
        await WaitForPollAsync(sim);

        // 各自 IOU
        var aliceIou = await sim.Bridge.GetQuoteBalanceAsync("alice-account");
        var bobIou = await sim.Bridge.GetQuoteBalanceAsync("bob-account");
        Assert.Equal(1_000_000_000UL, aliceIou);
        Assert.Equal(2_000_000_000UL, bobIou);

        // alice 提现——bob 不受影响
        await sim.Bridge.WithdrawAsync("alice-account", alice.Address, 500_000_000UL);
        await WaitForBlockAsync(sim, 5);
        await WaitForPollAsync(sim);

        aliceIou = await sim.Bridge.GetQuoteBalanceAsync("alice-account");
        bobIou = await sim.Bridge.GetQuoteBalanceAsync("bob-account");
        Assert.Equal(500_000_000UL, aliceIou);
        Assert.Equal(2_000_000_000UL, bobIou);  // bob 不动
    }

    // —— 提现钩子（venue 接线——venue 扣减在签名之前）——

    [Fact]
    public async Task 提现钩子拒绝_不借记不出金()
    {
        using var sim = await BridgeLoopback.StartAsync("hook-reject");
        var agent = LedgerHarness.Wallet("hook-reject-agent");
        sim.Bridge.WithdrawHook = new StubHook(ok: false, error: "可用现金不足：账户 nobody");

        var ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => sim.Bridge.WithdrawAsync("nobody", agent.Address, 1_000_000UL));
        Assert.Contains("可用现金不足", ex.Message);

        // 拒绝是无残留的：IOU 不动、链上无出金
        Assert.Equal(0UL, await sim.Bridge.GetQuoteBalanceAsync("nobody"));
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(0UL, custody.QuoteBalanceAtoms);
    }

    [Fact]
    public async Task 钩子放行_IOU不足_clamp到零不underflow()
    {
        using var sim = await BridgeLoopback.StartAsync("hook-clamp");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("hook-clamp-agent");
        sim.Bridge.WithdrawHook = new StubHook(ok: true);

        // buyer 充 1 AQ、other 充 2 AQ（custody 合计 3 AQ——链上够付）
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 1_000_000_000UL, "buyer", asset: Quote);
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 2_000_000_000UL, "other", asset: Quote);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        // buyer 提 3 AQ（钩子放行——venue 现金闸门语义）：IOU 1−3 < 0 ⇒ clamp 0（旧版 ulong underflow 成 1.8e19）
        await sim.Bridge.WithdrawAsync("buyer", agent.Address, 3_000_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);

        Assert.Equal(0UL, await sim.Bridge.GetQuoteBalanceAsync("buyer"));   // clamp——「净提取史」停在 0
        Assert.Equal(2_000_000_000UL, await sim.Bridge.GetQuoteBalanceAsync("other"));   // other 不动
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(0UL, custody.QuoteBalanceAtoms);   // 3 AQ 全数出金
        var agentBalance = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(3_000_000_000UL, agentBalance.QuoteBalanceAtoms);
    }

    /// <summary>钩子桩：固定放行/拒绝（桥的钩子协议单测——真实 venue 扣减见 LedgerSpotLinkTests）。</summary>
    private sealed class StubHook(bool ok, string? error = null) : ILedgerWithdrawHook
    {
        public Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(
            string accountKey, ulong amountAtoms, uint asset = LedgerSupply.Assets.Quote, CancellationToken ct = default)
            => Task.FromResult((ok, error));
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
    {
        await Task.Delay(700);   // > 500ms 轮询间隔
    }
}
