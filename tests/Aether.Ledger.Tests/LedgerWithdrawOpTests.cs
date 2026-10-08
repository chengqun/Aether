using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;

namespace Aether.Ledger.Tests;

/// <summary>
/// 提币 wire op（批 4 缺口，2026-10-08——客户端操作面）：JSON-RPC <c>withdraw</c> 全栈 E2E——
/// SimRpcClient（登录绑定账户）→ op 分发 → LedgerWithdrawGlue → 桥（烧持仓 + custody 签名出金）→ 链。
/// 另钉两个 wire 闸门：未登录 90001 / 桥未配置 90019。
/// </summary>
public class LedgerWithdrawOpTests
{
    private const string Instrument = "ax001";
    private static readonly DateTime SpotNow = new(2026, 9, 23, 10, 0, 0);

    private sealed record WithdrawStack(BridgeLoopback Sim, SimTradingCore Core, LedgerSpotLink Link,
        SimRpcServer Server, SimRpcClient Client) : IDisposable
    {
        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            try { Link.Dispose(); } catch { }
            try { Server.Dispose(); } catch { }
            try { Sim.Dispose(); } catch { }
            try { Core.Dispose(); } catch { }
        }
    }

    /// <summary>全栈：链回环 + 纯内存核心（ax001 上板）+ 桥 + 接线 + RPC 服务端（提币 handler 注入）+ 客户端。</summary>
    private static async Task<WithdrawStack> StartAsync(bool withBridge = true)
    {
        var sim = await BridgeLoopback.StartAsync("withdraw-op");
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { Instrument } },
            new SimClock(() => SpotNow));
        await core.InitializeAsync();
        var link = withBridge
            ? new LedgerSpotLink(core, sim.Bridge, sim.Client, Instrument, reconcileInterval: TimeSpan.FromSeconds(30))
            : null;
        if (link is not null) sim.Bridge.WithdrawHook = link;
        var server = new SimRpcServer(core, "127.0.0.1", 0, link is null ? null : LedgerWithdrawGlue.Wrap(sim.Bridge));
        server.Start();
        var port = (server.LocalEndpoint as System.Net.IPEndPoint)!.Port;
        var client = new SimRpcClient("127.0.0.1", port);
        await client.ConnectAsync();
        if (link is not null) await link.StartAsync();
        return new WithdrawStack(sim, core, link!, server, client);
    }

    private static int LongVolume(SimTradingCore core, string account)
        => core.QueryPositions(account, Instrument).Where(p => p.PosiDirection == CtpPosiDirection.Long)
            .Sum(p => p.TotalPosition);

    [Fact]
    public async Task 提币op全链路_登录绑定_烧持仓_出金()
    {
        using var stack = await StartAsync();
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("withdraw-op-agent");

        // 充值 3 币到 u1 → 对账补铸 3 手
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address, 300_000_000UL, "u1");
        await WaitBlockAsync(sim, 1);
        await Task.Delay(700);
        stack.Link.ReconcileNow();
        Assert.Equal(3, LongVolume(stack.Core, "u1"));

        // 登录绑定 u1（凭据即鉴权——绑定账户 = 出金账户）→ 提币 1 币
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));
        var txHash = await stack.Client.WithdrawAsync(agent.Address.Hex, 100_000_000UL);
        Assert.NotEmpty(txHash);
        await WaitBlockAsync(sim, 2);
        await Task.Delay(700);
        stack.Link.ReconcileNow();

        Assert.Equal(2, LongVolume(stack.Core, "u1"));                     // 烧毁 1 手
        Assert.Equal(2, stack.Core.GetSpotMintedVolumes(Instrument)["u1"]);
        Assert.Equal(200_000_000UL, await sim.Bridge.GetCoinBalanceAsync("u1"));   // 3 币 − 1 币（atoms）
        var custody = await sim.Client.GetBalanceAsync(sim.Custody.Address.Hex);
        Assert.Equal(200_000_000UL, custody.BalanceAtoms);                 // 链上出金 1 币
        var agentBalance = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(100_000_000UL, agentBalance.BalanceAtoms);            // 到账
    }

    [Fact]
    public async Task 提币op_未登录拒绝90001()
    {
        using var stack = await StartAsync();
        var agent = LedgerHarness.Wallet("withdraw-op-nologin");

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(agent.Address.Hex, 100_000_000UL));
        Assert.Equal(SimErrorCodes.NotLoggedIn, ex.ErrorId);
    }

    [Fact]
    public async Task 提币op_桥未配置诚实拒绝90019()
    {
        using var stack = await StartAsync(withBridge: false);   // 无桥部署（未设 custody seed 的交易所）
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(LedgerHarness.Wallet("x").Address.Hex, 100_000_000UL));
        Assert.Equal(SimErrorCodes.WithdrawalFailed, ex.ErrorId);
        Assert.Contains("AETHER_LEDGER_CUSTODY_SEED", ex.ErrorMsg);
    }

    [Fact]
    public async Task 提币op_持仓不足_90019消息透传()
    {
        using var stack = await StartAsync();
        var agent = LedgerHarness.Wallet("withdraw-op-poor");
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));

        // u1 没充过值——venue 无持仓可烧 → 桥拒（消息带原因，ErrorId 90019 透传）
        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(agent.Address.Hex, 100_000_000UL));
        Assert.Equal(SimErrorCodes.WithdrawalFailed, ex.ErrorId);
        Assert.Contains("提币", ex.ErrorMsg);
    }

    /// <summary>等出块（高度达到 target）。</summary>
    private static async Task WaitBlockAsync(BridgeLoopback sim, ulong targetHeight, int timeoutMs = 8000)
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
}
