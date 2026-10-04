using Acme.Ctp.Front;
using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── CTP 本地仿真柜台（独立服务端进程）────────────────────────────────────────
// 单独起一个柜台进程：dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host
// 客户端（Aether 桌面 app）一律官方 SDK 链路：期货页登录窗口前置地址填 tcp://127.0.0.1:17801/17802
// 直连本前置（与连 SimNow 同形）；JSON-RPC :17800 是仓内备用路径（E2E 参考客户端 RemoteCtpSession 用）。
//
// 配置（全部在本进程读——客户端侧零必设项，柜台由前置地址选）：
//   AETHER_CTP_SIM_ADDR    监听端点（默认 127.0.0.1:17800，格式 [host:]port）
//   AETHER_CTP_SIM_DB      账本库路径（默认 ~/.aether/ctp-sim.db；与真实 ctp.db 分文件）
//   AETHER_CTP_SIM_BALANCE 新账户初始资金
//   AETHER_CTP_SIM_BOARD   上市合约表（逗号分隔合约号；默认 zx001——「我就是交易所」的自有品种（2 字母官方命名合规），
//                          自定义 id 经 解析链：精确→品种派生→合成 兜底；坏值容忍回默认）
//   AETHER_CTP_SIM_LOGIN_MAX_FAILURES / AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC  防爆破（登录锁定）
//   AETHER_CTP_FRONT_PORT / AETHER_CTP_MD_FRONT_PORT  CTP 协议前置双腿端口（默认 17801/17802）
//   AETHER_CTP_SIM_LOG     日志文件路径（默认空=仅控制台；Windows 服务形态部署时由 setup 脚本设置）
//
// 价格源恒为订单簿价格发现（2026-10-03 起 walk/回放已删除）：行情 = 簿顶 + 成交流，tick 事件驱动；
// 流动性由外部客户端策略（labs leanalgorithms 做市/噪声 bot）供给——不起 bot = 空闲市场无对手价（真实交易所本义）。
// 行情随柜台：sim 登录的行情恒为本地产生（按订阅推送）——无环境变量开关。
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();   // 服务器部署形态：SCM 启动 → ServiceLifetime（服务停止 = 优雅关停 flush 账本）；
                                        // 交互运行（dotnet run）仍是 console lifetime，本地行为零变

var opts = SimHostOptions.FromEnvironment();
var core = new SimTradingCore(opts.Core);
var server = new SimRpcServer(core, opts.Host, opts.Port);
var ctpFrontPort = int.TryParse(Environment.GetEnvironmentVariable("AETHER_CTP_FRONT_PORT"), out var fp) ? fp : 17801;
var ctpFront = new CtpFrontServer(core, opts.Host, ctpFrontPort, msg => Log.Line($"[CTP-Front] {msg}"));
var ctpMdPort = int.TryParse(Environment.GetEnvironmentVariable("AETHER_CTP_MD_FRONT_PORT"), out var mfp) ? mfp : 17802;
var ctpMdFront = new CtpFrontMdServer(core, opts.Host, ctpMdPort, msg => Log.Line($"[CTP-Front-MD] {msg}"));

builder.Services.AddHostedService(_ => new SimCounterHostedService(core, server, opts, ctpFront, ctpMdFront));
await builder.Build().RunAsync();

/// <summary>柜台生命周期：启动 = 恢复账本 + 双协议监听（JSON RPC + CTP 前置）；停止 = 关连接 + 落盘。</summary>
internal sealed class SimCounterHostedService(SimTradingCore core, SimRpcServer server, SimHostOptions opts, CtpFrontServer ctpFront, CtpFrontMdServer ctpMdFront)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await core.InitializeAsync(ct);   // 重启恢复：挂单作废、报单号/成交号序列续排（host 启动时一次）

        server.Start();
        ctpFront.Start();   // CTP 协议前置·交易腿（第三方客户端接入——快期/文华/官方 SDK 直连）
        ctpMdFront.Start(); // CTP 协议前置·行情腿（M3：官方 SDK 行情订阅 + tick 推送）

        var port = server.LocalEndpoint?.Port ?? opts.Port;
        var ctpPort = ctpFront.LocalEndpoint?.Port ?? 17801;
        var listed = core.QueryListedInstruments();
        var accounts = core.QueryAccounts();
        Log.Line($"[CTP-SIM] 本地仿真柜台已就绪：{opts.Host}:{port}（JSON RPC）+ :{ctpPort}（CTP 前置）");
        Log.Line($"[CTP-SIM]   账本：{opts.Core.DatabasePath ?? "纯内存（不落盘）"}");
        if (accounts.Count > 0)
            Log.Line($"[CTP-SIM]   账户 {accounts.Count} 个（{accounts.Count(a => a.HasPassword)} 个已设密码）：{string.Join(",", accounts.Select(a => a.AccountId))}");
        Log.Line($"[CTP-SIM]   行情 交易所模式：订单簿 + 成交驱动（价格发现归簿）| 做市/噪声 = 外部客户端策略（labs leanalgorithms）| 新账户初始资金 {opts.Core.InitialBalance}");
        Log.Line($"[CTP-SIM]   上市合约 {listed.Count} 个：{string.Join(",", listed.Select(i => i.InstrumentId))}");
        Log.Line("[CTP-SIM] Ctrl+C 退出（账本自动落盘）。Aether 客户端：前置地址填 sim。CTP 客户端：交易腿 tcp://<本机IP>:" + ctpPort + "，行情腿 tcp://<本机IP>:" + (ctpMdFront.LocalEndpoint?.Port ?? 17802));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await server.StopAsync();
        await ctpFront.StopAsync();
        await ctpMdFront.StopAsync();
        core.Dispose();   // 同步 flush（重启续用靠这里落盘）
        Log.Line("[CTP-SIM] 已退出，账本已落盘。");
    }
}

/// <summary>
/// 最小可观测性：恒写控制台；<c>AETHER_CTP_SIM_LOG</c> 设了路径时（Windows 服务形态——headless 无控制台可见）
/// 追加到文件。日志失败不致命（try/catch 吞）——遥测自身故障绝不打断柜台。
/// </summary>
internal static class Log
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AETHER_CTP_SIM_LOG");

    public static void Line(string msg)
    {
        Console.WriteLine(msg);
        if (LogPath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { /* 磁盘/权限故障不打断柜台 */ }
    }
}
