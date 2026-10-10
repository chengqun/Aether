using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;

namespace Aether.Ledger.Tests;

/// <summary>
/// 提现 wire op 全栈 E2E（2026-10-10 币通道退役后：**AQ 单资产**——扣场内可用现金 + custody 出金 AQ）：
/// SimRpcClient（登录绑定账户）→ op 分发 → LedgerWithdrawGlue → 桥 → 链。
/// 另钉 wire 闸门：未登录 90001 / 桥未配置 90019 / coin 资产已退役 90019。
/// </summary>
public class LedgerWithdrawOpTests
{
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

    /// <summary>全栈：链回环 + 纯内存核心（ax001 上板）+ 桥 + 接线 + RPC 服务端（提现 handler 注入）+ 客户端。</summary>
    private static async Task<WithdrawStack> StartAsync(bool withBridge = true)
    {
        var sim = await BridgeLoopback.StartAsync("withdraw-op");
        var core = new SimTradingCore(
            new SimOptions { DatabasePath = null, ListedInstruments = new[] { "ax001" } },
            new SimClock(() => SpotNow));
        await core.InitializeAsync();
        var link = withBridge
            ? new LedgerSpotLink(core, sim.Bridge, reconcileInterval: TimeSpan.FromSeconds(30))
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

    [Fact]
    public async Task 提现op全链路_登录绑定_扣现金_出金AQ()
    {
        using var stack = await StartAsync();
        var sim = stack.Sim;
        var treasury = new LedgerWallet(Convert.FromHexString(sim.TreasurySeed));
        var agent = LedgerHarness.Wallet("withdraw-op-agent");

        // 充 30 AQ 到 u1 → 对账 → 现金 30
        await LedgerBridge.DepositAsync(sim.Client, treasury, sim.Custody.Address,
            3_000_000_000UL, "u1", LedgerSupply.Assets.Quote);
        await WaitBlockAsync(sim, 1);
        await Task.Delay(700);
        stack.Link.ReconcileNow();
        Assert.Equal(30, stack.Core.QueryAccount("u1").Balance, 2);

        // 登录绑定 u1（凭据即鉴权——绑定账户 = 出金账户）→ 提现 10 AQ
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));
        var txHash = await stack.Client.WithdrawAsync(agent.Address.Hex, 1_000_000_000UL, "quote");
        Assert.NotEmpty(txHash);
        await WaitBlockAsync(sim, 2);
        await Task.Delay(700);
        stack.Link.ReconcileNow();

        Assert.Equal(20, stack.Core.QueryAccount("u1").Balance, 2);                     // 现金扣减
        Assert.Equal(2_000_000_000UL, await sim.Bridge.GetQuoteBalanceAsync("u1"));     // AQ IOU 同步
        var agentChain = await sim.Client.GetBalanceAsync(agent.Address.Hex);
        Assert.Equal(1_000_000_000UL, agentChain.QuoteBalanceAtoms);                    // 10 AQ 到自持钱包
    }

    [Fact]
    public async Task 提现op_coin资产_币通道已退役拒绝()
    {
        using var stack = await StartAsync();
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));

        // coin 提现（旧客户端默认资产）——桥层拒，90019 消息带「币通道已退役」
        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(LedgerHarness.Wallet("x").Address.Hex, 100_000_000UL, "coin"));
        Assert.Equal(SimErrorCodes.WithdrawalFailed, ex.ErrorId);
        Assert.Contains("币通道已退役", ex.ErrorMsg);
    }

    [Fact]
    public async Task 提现op_现金不足_90019消息透传()
    {
        using var stack = await StartAsync();
        var agent = LedgerHarness.Wallet("withdraw-quote-poor");
        // 无 AQ 充值 + 夹具无 InitialBalance（默认 0）⇒ 零现金——提巨额必拒：
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(agent.Address.Hex, 999_999_000_000UL, "quote"));
        Assert.Equal(SimErrorCodes.WithdrawalFailed, ex.ErrorId);
        Assert.Contains("可提现金不足", ex.ErrorMsg);
    }

    [Fact]
    public async Task 提现op_未登录拒绝90001()
    {
        using var stack = await StartAsync();
        var agent = LedgerHarness.Wallet("withdraw-op-nologin");

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(agent.Address.Hex, 100_000_000UL, "quote"));
        Assert.Equal(SimErrorCodes.NotLoggedIn, ex.ErrorId);
    }

    [Fact]
    public async Task 提现op_桥未配置诚实拒绝90019()
    {
        using var stack = await StartAsync(withBridge: false);   // 无桥部署（未设 custody seed 的交易所）
        await stack.Client.LoginAsync(new LoginArg("SIM", "u1", "任意"));

        var ex = await Assert.ThrowsAsync<CtpBusinessException>(
            () => stack.Client.WithdrawAsync(LedgerHarness.Wallet("x").Address.Hex, 100_000_000UL, "quote"));
        Assert.Equal(SimErrorCodes.WithdrawalFailed, ex.ErrorId);
        Assert.Contains("AETHER_LEDGER_CUSTODY_SEED", ex.ErrorMsg);
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
