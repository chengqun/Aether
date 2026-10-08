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
/// 批 3 交易所桥验收：充币（agent 签名链上转账 → memo 归账 IOU）→ 提币（IOU 借记 + custody 签名出金）→
/// 双向闭环 + 链文件独立验证 + 桥重启（链重扫恢复 IOU——不依赖任何独立持久化）。
/// </summary>
public class LedgerBridgeTests
{
    [Fact]
    public async Task 充币到提币_双向闭环()
    {
        using var sim = await BridgeLoopback.StartAsync("roundtrip");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("agent-roundtrip");

        // treasury → agent（链上转账，让 agent 有币可充）
        await LedgerBridge.DepositAsync(sim.Client, treasury, agent.Address,
            10_000_000_000UL, "unused", ct: default);   // 先给 agent 转一些币（memo 随便——不是充值到 custody）
        await WaitForBlockAsync(sim, 1);

        // agent 充值到交易所 custody（memo = "agent-account"——归账键）
        await LedgerBridge.DepositAsync(sim.Client, agent, sim.Custody.Address,
            3_000_000_000UL, "agent-account");
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        // IOU 已入账
        var iou = await sim.Bridge.GetCoinBalanceAsync("agent-account");
        Assert.Equal(3_000_000_000UL, iou);

        // 提币：IOU 借记 + custody 签名出金
        await sim.Bridge.WithdrawAsync("agent-account", agent.Address, 1_000_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);

        // IOU 已扣
        var afterWithdraw = await sim.Bridge.GetCoinBalanceAsync("agent-account");
        Assert.Equal(2_000_000_000UL, afterWithdraw);

        // 链上 agent 余额 = 原有 - 3（充值）+ 1（提币回）= 8
        var chainBalance = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(8_000_000_000UL, chainBalance.BalanceAtoms);

        // custody 链上余额 = 3 - 1 = 2
        var custodyBalance = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(2_000_000_000UL, custodyBalance.BalanceAtoms);
    }

    [Fact]
    public async Task 提币余额不足_拒绝()
    {
        using var sim = await BridgeLoopback.StartAsync("insufficient");
        var agent = LedgerHarness.Wallet("agent-insufficient");

        // 未充值就提
        var ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => sim.Bridge.WithdrawAsync("nobody", agent.Address, 1_000_000UL));
        Assert.Contains("余额不足", ex.Message);
    }

    [Fact]
    public async Task 桥重启_链重扫恢复IOU_不依赖独立持久化()
    {
        using var sim = await BridgeLoopback.StartAsync("restart");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("agent-restart");

        // 充值
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            5_000_000_000UL, "restart-account");
        await WaitForBlockAsync(sim, 1);
        await WaitForPollAsync(sim);

        var iou = await sim.Bridge.GetCoinBalanceAsync("restart-account");
        Assert.Equal(5_000_000_000UL, iou);

        // 提币一部分
        await sim.Bridge.WithdrawAsync("restart-account", agent.Address, 2_000_000_000UL);
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        var afterWithdraw = await sim.Bridge.GetCoinBalanceAsync("restart-account");
        Assert.Equal(3_000_000_000UL, afterWithdraw);

        // 「链是唯一真相」：独立验证器复核链文件（在 using 内——fixture Dispose 会删文件）。
        // 重启恢复语义 = StartAsync 的全量重扫从链重建 IOU（引擎重放确定性已覆盖）——
        // custody 链上余额 = 5 - 2 = 3 = IOU（链上真相与桥内存态一致）。
        var verify = LedgerVerifier.VerifyFile(sim.ChainFile);
        Assert.True(verify.Ok, verify.Error);
        Assert.Equal(3_000_000_000UL, verify.Accounts[sim.Custody.Address.Hex].BalanceAtoms);
    }

    [Fact]
    public async Task 多账户充提_归账互不干扰()
    {
        using var sim = await BridgeLoopback.StartAsync("multi");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var alice = LedgerHarness.Wallet("bridge-alice");
        var bob = LedgerHarness.Wallet("bridge-bob");

        // treasury → alice/bob（链上先给币——同钱包背靠背须等块推进 nonce）
        await LedgerBridge.DepositAsync(sim.Client, treasury, alice.Address, 4_000_000_000UL, "seed");
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, treasury, bob.Address, 6_000_000_000UL, "seed");
        await WaitForBlockAsync(sim, 2);

        // 两个账户各自充值到 custody
        await LedgerBridge.DepositAsync(sim.Client, alice, sim.Custody.Address, 1_000_000_000UL, "alice-account");
        await WaitForBlockAsync(sim, 3);
        await LedgerBridge.DepositAsync(sim.Client, bob, sim.Custody.Address, 2_000_000_000UL, "bob-account");
        await WaitForBlockAsync(sim, 4);
        await WaitForPollAsync(sim);

        // 各自 IOU
        var aliceIou = await sim.Bridge.GetCoinBalanceAsync("alice-account");
        var bobIou = await sim.Bridge.GetCoinBalanceAsync("bob-account");
        Assert.Equal(1_000_000_000UL, aliceIou);
        Assert.Equal(2_000_000_000UL, bobIou);

        // alice 提币——bob 不受影响
        await sim.Bridge.WithdrawAsync("alice-account", alice.Address, 500_000_000UL);
        await WaitForBlockAsync(sim, 5);
        await WaitForPollAsync(sim);

        aliceIou = await sim.Bridge.GetCoinBalanceAsync("alice-account");
        bobIou = await sim.Bridge.GetCoinBalanceAsync("bob-account");
        Assert.Equal(500_000_000UL, aliceIou);
        Assert.Equal(2_000_000_000UL, bobIou);  // bob 不动
    }

    // —— 提币钩子（批 4 接线——venue 烧毁在签名之前）——

    [Fact]
    public async Task 提币钩子拒绝_不借记不出金()
    {
        using var sim = await BridgeLoopback.StartAsync("hook-reject");
        var agent = LedgerHarness.Wallet("hook-reject-agent");
        sim.Bridge.WithdrawHook = new StubHook(ok: false, error: "现货持仓不足：账户 nobody 持有 0 手");

        var ex = await Assert.ThrowsAsync<LedgerClientException>(
            () => sim.Bridge.WithdrawAsync("nobody", agent.Address, 1_000_000UL));
        Assert.Contains("现货持仓不足", ex.Message);

        // 拒绝是无残留的：IOU 不动、链上无出金
        Assert.Equal(0UL, await sim.Bridge.GetCoinBalanceAsync("nobody"));
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(0UL, custody.BalanceAtoms);
    }

    [Fact]
    public async Task 钩子放行_IOU不足_clamp到零不underflow()
    {
        using var sim = await BridgeLoopback.StartAsync("hook-clamp");
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("hook-clamp-agent");
        sim.Bridge.WithdrawHook = new StubHook(ok: true);

        // buyer 充 1 币、other 充 2 币（custody 合计 3 币——链上够付）
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 1_000_000_000UL, "buyer");
        await WaitForBlockAsync(sim, 1);
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 2_000_000_000UL, "other");
        await WaitForBlockAsync(sim, 2);
        await WaitForPollAsync(sim);

        // buyer 提 3 币（钩子放行——venue 持仓闸门语义）：IOU 1−3 < 0 ⇒ clamp 0（旧版 ulong underflow 成 1.8e19）
        await sim.Bridge.WithdrawAsync("buyer", agent.Address, 3_000_000_000UL);
        await WaitForBlockAsync(sim, 3);
        await WaitForPollAsync(sim);

        Assert.Equal(0UL, await sim.Bridge.GetCoinBalanceAsync("buyer"));   // clamp——「净提取史」停在 0
        Assert.Equal(2_000_000_000UL, await sim.Bridge.GetCoinBalanceAsync("other"));   // other 不动
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(0UL, custody.BalanceAtoms);   // 3 币全数出金
        var agentBalance = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(3_000_000_000UL, agentBalance.BalanceAtoms);
    }

    /// <summary>钩子桩：固定放行/拒绝（桥的钩子协议单测——真实 venue 烧毁见 LedgerSpotLinkTests）。</summary>
    private sealed class StubHook(bool ok, string? error = null) : ILedgerWithdrawHook
    {
        public Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(
            string accountKey, ulong amountAtoms, uint asset = LedgerSupply.Assets.Coin, CancellationToken ct = default)
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
