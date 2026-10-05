using Acme.Ctp.Sim.Server;
using Acme.Ctp.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── CTP 本地仿真交易所（独立服务端进程——2026-10-05 起与前置分离，真实世界对齐）────────────
// 单独起交易所：dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host
// 前置（券商侧接入层）是独立进程：dotnet run --project apps/CtpSim/Acme.Ctp.Front.Host
// （连接本交易所 127.0.0.1:17800，对客户端监听 17801/17802——详见该 host 头注释）
// 客户端（Aether 桌面 app）一律官方 SDK 链路：期货页登录窗口前置地址填 tcp://127.0.0.1:17801/17802。
//
// 配置（全部在本进程读）：
//   AETHER_CTP_SIM_ADDR    监听端点（默认 127.0.0.1:17800，格式 [host:]port）——进程分离后交易所
//                          只服务前置进程（同机 127.0.0.1 即可；服务器部署脚本即此默认，17800 无需对外）
//   AETHER_CTP_SIM_DB      账本库路径（默认 ~/.aether/ctp-sim.db；与真实 ctp.db 分文件）
//   AETHER_CTP_SIM_BALANCE 新账户初始资金
//   AETHER_CTP_SIM_BOARD   上市合约表（逗号分隔合约号；默认 zx001——「我就是交易所」的自有品种（2 字母官方命名合规），
//                          自定义 id 经 解析链：精确→品种派生→合成 兜底；坏值容忍回默认）
//   AETHER_CTP_SIM_LOGIN_MAX_FAILURES / AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC  防爆破（登录锁定）
//
// 价格源恒为订单簿价格发现（2026-10-03 起 walk/回放已删除）：行情 = 簿顶 + 成交流，tick 事件驱动；
// 流动性由外部客户端策略（labs leanalgorithms 做市/噪声 bot）供给——不起 bot = 空闲市场无对手价（真实交易所本义）。
// 行情随柜台：sim 登录的行情恒为本地产生（按订阅推送）——无环境变量开关。
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

var opts = SimHostOptions.FromEnvironment();
var core = new SimTradingCore(opts.Core);
var server = new SimRpcServer(core, opts.Host, opts.Port);
builder.Services.AddHostedService(_ => new ExchangeHostedService(core, server, opts));
await builder.Build().RunAsync();

/// <summary>交易所生命周期：启动 = 恢复账本 + RPC 监听（前置进程与 E2E 参考客户端连此端口）；停止 = 关连接 + 落盘。</summary>
internal sealed class ExchangeHostedService(SimTradingCore core, SimRpcServer server, SimHostOptions opts)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await core.InitializeAsync(ct);   // 重启恢复：挂单作废、报单号/成交号序列续排（host 启动时一次）
        server.Start();
        var port = server.LocalEndpoint?.Port ?? opts.Port;
        var listed = core.QueryListedInstruments();
        var accounts = core.QueryAccounts();
        Console.WriteLine($"[CTP-SIM] 本地仿真交易所已就绪：{opts.Host}:{port}（JSON-RPC——前置进程/E2E 客户端连接口）");
        Console.WriteLine($"[CTP-SIM]   账本：{opts.Core.DatabasePath ?? "纯内存（不落盘）"}");
        if (accounts.Count > 0)
            Console.WriteLine($"[CTP-SIM]   账户 {accounts.Count} 个（{accounts.Count(a => a.HasPassword)} 个已设密码）：{string.Join(",", accounts.Select(a => a.AccountId))}");
        Console.WriteLine($"[CTP-SIM]   行情 交易所模式：订单簿 + 成交驱动（价格发现归簿）| 做市/噪声 = 外部客户端策略（labs leanalgorithms）| 新账户初始资金 {opts.Core.InitialBalance}");
        Console.WriteLine($"[CTP-SIM]   上市合约 {listed.Count} 个：{string.Join(",", listed.Select(i => i.InstrumentId))}");
        Console.WriteLine("[CTP-SIM] Ctrl+C 退出（账本自动落盘）。前置进程：dotnet run --project apps/CtpSim/Acme.Ctp.Front.Host（连接 127.0.0.1:17800）。");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await server.StopAsync();
        core.Dispose();   // 同步 flush（重启续用靠这里落盘）
        Console.WriteLine("[CTP-SIM] 交易所已退出，账本已落盘。");
    }
}
