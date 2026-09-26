using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── CTP 本地仿真柜台（独立服务端进程）────────────────────────────────────────
// 单独起一个柜台进程：dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host
// 客户端（Aether 桌面 app）无需任何环境变量：期货页登录窗口前置地址填 sim ⇒ ICtpSession
// 经 RoutingCtpSession 路由到薄 RPC 客户端连 127.0.0.1:17800；填真实前置 ⇒ 官方链路。
//
// 配置（全部在本进程读——客户端侧零必设项，柜台由前置地址选）：
//   AETHER_CTP_SIM_ADDR    监听端点（默认 127.0.0.1:17800，格式 [host:]port）
//   AETHER_CTP_SIM_DB      账本库路径（默认 ~/.aether/ctp-sim.db；与真实 ctp.db 分文件）
//   AETHER_CTP_SIM_TICK_MS 行情推进间隔（0 = 不自动喂）
//   AETHER_CTP_SIM_SEED    随机游走种子（默认固定 ⇒ 可复现）
//   AETHER_CTP_SIM_BALANCE 新账户初始资金
//   AETHER_CTP_SIM_BOARD   上市合约表（逗号分隔合约号；默认 9 个种子合约——「我就是交易所」的自定义上架面，
//                          自定义 id 经 解析链：精确→品种派生→合成 兜底；坏值容忍回默认）
// 行情随柜台：sim 登录的行情恒为本地合成（按订阅推送）——无环境变量开关。
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

var opts = SimHostOptions.FromEnvironment();
var core = new SimTradingCore(opts.Core);
var server = new SimRpcServer(core, opts.Host, opts.Port);

builder.Services.AddHostedService(_ => new SimCounterHostedService(core, server, opts));
await builder.Build().RunAsync();

/// <summary>柜台生命周期：启动 = 恢复账本 + 起行情 feed + 监听；停止 = 关连接 + 落盘（Ctrl+C 触发）。</summary>
internal sealed class SimCounterHostedService(SimTradingCore core, SimRpcServer server, SimHostOptions opts)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await core.InitializeAsync(ct);   // 重启恢复：挂单作废、报单号/成交号序列续排（host 启动时一次）
        core.StartMarketFeed();
        server.Start();

        var port = server.LocalEndpoint?.Port ?? opts.Port;
        var listed = core.QueryListedInstruments();
        Console.WriteLine($"[CTP-SIM] 本地仿真柜台已就绪：{opts.Host}:{port}");
        Console.WriteLine($"[CTP-SIM]   账本：{opts.Core.DatabasePath ?? "纯内存（不落盘）"}");
        Console.WriteLine($"[CTP-SIM]   tick 间隔 {opts.Core.TickIntervalMs}ms | 种子 {opts.Core.Seed} | 新账户初始资金 {opts.Core.InitialBalance} | 行情 本地合成（按订阅推送——行情随柜台）");
        Console.WriteLine($"[CTP-SIM]   上市合约 {listed.Count} 个：{string.Join(",", listed.Select(i => i.InstrumentId))}");
        Console.WriteLine("[CTP-SIM] Ctrl+C 退出（账本自动落盘）。客户端：期货页登录前置地址填 sim 即连本柜台（无需环境变量）。");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await server.StopAsync();
        core.Dispose();   // 停 feed + 同步 flush（重启续用靠这里落盘）
        Console.WriteLine("[CTP-SIM] 已退出，账本已落盘。");
    }
}
