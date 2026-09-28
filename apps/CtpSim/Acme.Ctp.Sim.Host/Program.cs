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
//   AETHER_CTP_SIM_BOARD   上市合约表（逗号分隔合约号；默认 sim001——「我就是交易所」的自有品种，
//                          自定义 id 经 解析链：精确→品种派生→合成 兜底；坏值容忍回默认）
//   AETHER_CTP_SIM_REPLAY        历史 tick 回放 CSV 路径（确定性测试的价格源——设定即回放模式，优先于 MODE；
//                                 SimHostOptions 强制归一 walk（回放骑 walk 撮合机制）；启动失败 ⇒ 柜台退出不静默换源）
//   AETHER_CTP_SIM_REPLAY_SPEED  回放速度倍率（默认 1；>1 快放）
//   AETHER_CTP_SIM_REPLAY_LOOP   回放循环（默认 true——文件尾停 1s 再来）
//   回放 CSV 格式（首行表头）：time,instrument,bid,ask,last[,volume]——time 为 HH:mm:ss[.fff]（行间差 pacing）
//   AETHER_CTP_SIM_MODE          价格源模式：exchange = 交易所模式（订单簿驱动行情——价格发现归簿；
//                                 行情 = 簿顶 + 最新成交价，tick 事件驱动；庄家兜底关闭）——**默认**（2026-09-28
//                                 用户定调「我实际就是做一个交易所」）；walk = 随机游走（显式退路，旧行为）。
//                                 真实行情转发（接外部期货行情）明确不做——本柜台是自己的交易所。
//   AETHER_CTP_SIM_MM            交易所模式做市机器人（默认 on——纯簿模式无人报价时市场全死；off = 纯净交易所）
//   AETHER_CTP_SIM_MM_SPREAD     做市报价点差（最小变动价位数，围绕最新成交价 ±N；默认 2）
//   AETHER_CTP_SIM_MM_VOLUME     做市单侧挂单手数（默认 5）
//   —— 做市商义务（交易所约束层：策略层机器人自便，约束层人人须守）——
//   AETHER_CTP_SIM_MM_MAX_SPREAD 最大报价价差（tick 数，默认 4——机器人想挂更宽也会被钳到该值）
//   AETHER_CTP_SIM_MM_MIN_VOLUME 单侧最小报价量（手，默认 1）
//   AETHER_CTP_SIM_MM_MIN_RATIO  双边连续报价时间占比目标（监测口径，默认 0.9；触限单边期豁免）
//   AETHER_CTP_SIM_MM_POS_LIMIT  做市商持仓限额（每合约单边手数，默认 200——触限停挂加仓侧、超限自动平仓减仓）
//   AETHER_CTP_SIM_MM_MONITOR_MS 义务监测采样间隔（毫秒，默认 1000）
//   —— 模拟成交（噪声交易者：随机吃做市流动力的程序化参与者——真实下单真实成交，价格经真实成交移动）——
//   AETHER_CTP_SIM_NOISE          模拟成交开关（默认 on；off = 静市——无人交易则盘口不动）
//   AETHER_CTP_SIM_NOISE_MS       噪声交易间隔毫秒（默认 2500，下限 500）
//   AETHER_CTP_SIM_NOISE_MAX_LOTS 噪声交易单笔手数上限（默认 3；净持仓软钳同值——持仓/OI 有界）
// 行情随柜台：sim 登录的行情恒为本地产生（按订阅推送）——无环境变量开关。
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

        // 价格源三选一（互斥，显式程度优先：REPLAY（给了 CSV 就是回放）> MODE=walk（显式退路）> 默认交易所模式）。
        // 回放失败 ⇒ 柜台退出（不静默换价格源——回放是显式意图，静默换成别的源会毁掉确定性测试）。
        var replayOn = false;
        if (!string.IsNullOrWhiteSpace(opts.Core.ReplayPath))
        {
            try
            {
                core.StartReplay(opts.Core.ReplayPath, opts.Core.ReplaySpeed, opts.Core.ReplayLoop);
                replayOn = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CTP-SIM] 回放启动失败，柜台未启动（不静默换价格源）：{ex.Message}");
                Console.Error.WriteLine("[CTP-SIM] 请检查 AETHER_CTP_SIM_REPLAY 的 CSV 路径与表头格式（time,instrument,bid,ask,last[,volume]）。");
                Environment.Exit(1);
            }
        }
        else if (opts.Core.MarketMode == SimMarketMode.Walk)
        {
            core.StartMarketFeed();   // 显式退路：随机游走（旧行为）
        }
        else
        {
            core.StartMarketMakerBot();   // 默认：交易所模式（做市机器人 AETHER_CTP_SIM_MM=off 可关——纯净交易所）
        }
        var exchangeOn = !replayOn && opts.Core.MarketMode == SimMarketMode.Exchange;

        server.Start();

        var port = server.LocalEndpoint?.Port ?? opts.Port;
        var listed = core.QueryListedInstruments();
        var accounts = core.QueryAccounts();
        Console.WriteLine($"[CTP-SIM] 本地仿真柜台已就绪：{opts.Host}:{port}");
        Console.WriteLine($"[CTP-SIM]   账本：{opts.Core.DatabasePath ?? "纯内存（不落盘）"}");
        if (accounts.Count > 0)
            Console.WriteLine($"[CTP-SIM]   账户 {accounts.Count} 个（{accounts.Count(a => a.HasPassword)} 个已设密码）：{string.Join(",", accounts.Select(a => a.AccountId))}");
        Console.WriteLine(exchangeOn
            ? $"[CTP-SIM]   行情 交易所模式：订单簿 + 成交驱动（价格发现归簿）| 做市机器人 {(opts.Core.MarketMakerBot ? $"开（点差 ±{opts.Core.MmSpreadTicks} tick × {opts.Core.MmVolume} 手）| 义务约束：价差≤{opts.Core.MmMaxSpreadTicks}t 量≥{opts.Core.MmMinQuoteVolume}手 占比≥{opts.Core.MmMinQuoteRatio:P0} 仓限{opts.Core.MmPositionLimit}/边（触限停加仓侧、超限自动平仓）" : "关（纯净簿——无人挂单即无对手价）")}{(opts.Core.MarketMakerBot && opts.Core.NoiseTrading ? $" | 模拟成交 噪声交易者（每 {Math.Max(500, opts.Core.NoiseIntervalMs)}ms 随机吃 ≤{opts.Core.NoiseMaxLots} 手）" : "")} | 新账户初始资金 {opts.Core.InitialBalance}"
            : replayOn
                ? $"[CTP-SIM]   行情 历史 tick 回放：{opts.Core.ReplayPath}（速度 ×{opts.Core.ReplaySpeed}，{(opts.Core.ReplayLoop ? "循环" : "单遍")}）"
                : $"[CTP-SIM]   tick 间隔 {opts.Core.TickIntervalMs}ms | 种子 {opts.Core.Seed} | 新账户初始资金 {opts.Core.InitialBalance} | 行情 本地合成（按订阅推送——行情随柜台）");
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
