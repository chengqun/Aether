using System.Text;
using Acme.Ctp.ProtoCapture;
using Ctp.Net.Core.Trader;
using Ctp.Net.Core.MarketData;

// ── CTP 协议抓包 spike（2026-09-28）───────────────────────────────────────────
// 目的：用本地官方 SDK 生成真实 CTP 协议流量，经记录代理捕获字节流，离线分析帧格式。
//
// 架构：
//   官方 SDK（CtpTrader/CtpMarketData，native 二进制）
//       ↓ TCP 连 127.0.0.1:17001/17002
//   RecordingProxy（纯字节管道 + 逐帧记录）
//       ↓ TCP 转发
//   真实前置（SimNow 或用户指定）
//
// 用法：
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- \
//     --front tcp://180.168.146.187:10130 \
//     --mdfront tcp://180.168.146.187:10131 \
//     --user <SimNow账号> --password <密码> --broker 9999
//
//   不带凭据也跑（连上 → 握手 → 登录失败——协议字节照样被捕获）。
//   SimNow 测试 AppId/AuthCode: simnow_client_test / 0000000000000000
//
//   直连模式（里程碑 1 前置服务器验收）——首参以 tcp: 开头，不走代理：
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <account> <password>
// ─────────────────────────────────────────────────────────────────────────────

// 首参 gen → 模板生成器（FrontGen）：从抓包生成 CtpFrontTemplates.cs。
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen [trade.txt路径] [输出.cs路径]
//   （两参可省略：抓包默认取帧数最多的目录；输出默认 <仓库根>/business/Ctp/Acme.Ctp.Front/）
var directArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (directArgs.Length > 0 && directArgs[0] == "gen")
{
    FrontGen.Run(
        directArgs.Length > 1 ? directArgs[1] : null,
        directArgs.Length > 2 ? directArgs[2] : null);
    return;
}

// 首参 tcp: 前缀 → 直连测试（DirectTest）：与抓包模式的 --xxx 选项风格互斥，分发无歧义
if (directArgs.Length > 0 && directArgs[0].StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
{
    await DirectTest.RunAsync(
        directArgs[0],
        directArgs.Length > 1 ? directArgs[1] : "testuser",
        directArgs.Length > 2 ? directArgs[2] : "testpass");
    return;
}

var tradeFront = Args(arg => arg == "--front", 1) ?? "tcp://180.168.146.187:10130";
var mdFront = Args(arg => arg == "--mdfront", 1) ?? "tcp://180.168.146.187:10131";
var userId = Args(arg => arg == "--user", 1) ?? "";
var password = Args(arg => arg == "--password", 1) ?? "";
var brokerId = Args(arg => arg == "--broker", 1) ?? "9999";
var durationSec = int.TryParse(Args(arg => arg == "--duration", 1), out var d) ? d : 30;

var outputDir = Path.Combine(Path.GetTempPath(), "ctp-proto-capture", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDir);
Console.WriteLine($"[Capture] 输出目录: {outputDir}");
Console.WriteLine($"[Capture] 交易前置: {tradeFront} → 127.0.0.1:17001");
Console.WriteLine($"[Capture] 行情前置: {mdFront} → 127.0.0.1:17002");
Console.WriteLine($"[Capture] 账号: {(string.IsNullOrEmpty(userId) ? "(空——捕获握手+登录失败协议)" : userId)}");
Console.WriteLine($"[Capture] 持续: {durationSec}s\n");

// 1. 起记录代理（交易 + 行情）
var tradeProxy = new RecordingProxy(17001, tradeFront, "Trade", Path.Combine(outputDir, "trade.txt"));
var mdProxy = new RecordingProxy(17002, mdFront, "MD", Path.Combine(outputDir, "md.txt"));
tradeProxy.Start();
mdProxy.Start();

// 2. 用官方 SDK 走代理跑一轮会话
var flowDir = Path.Combine(outputDir, "flow");
Directory.CreateDirectory(flowDir);

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

// —— 交易腿：连接 → 登录 →（成功则）查资金 → 挂/撤单 →（失败也行，协议字节已捕获）——
var tradeTask = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("\n[Trader] 启动（经代理 127.0.0.1:17001）…");
        using var trader = new CtpTrader(flowDir);
        trader.FrontConnected += (_, _) => Console.WriteLine("[Trader] 前置已连接");
        trader.FrontDisconnected += (_, reason) => Console.WriteLine($"[Trader] 前置断开: {reason}");
        trader.RspError += (_, e) => Console.WriteLine($"[Trader] 错误: [{e.ErrorId}] {e.ErrorMsg}");

        var loginReq = new CtpConnectRequest
        {
            FrontAddress = "tcp://127.0.0.1:17001",   // ← 指向代理
            BrokerId = brokerId,
            UserId = userId,
            Password = password,
            AppId = "simnow_client_test",
            AuthCode = "0000000000000000",
        };

        Console.WriteLine("[Trader] LoginAsync…");
        var loginResult = await trader.LoginAsync(loginReq, cts.Token);
        Console.WriteLine($"[Trader] 登录成功: 交易日={loginResult.TradingDay} FrontID={loginResult.FrontID} MaxOrderRef={loginResult.MaxOrderRef}");

        // 结算确认（新版 CTP 必须）
        Console.WriteLine("[Trader] ConfirmSettlement…");
        await trader.ConfirmSettlementAsync(brokerId, userId, cts.Token);
        Console.WriteLine("[Trader] 结算确认完成");

        // 查资金
        Console.WriteLine("[Trader] QueryTradingAccount…");
        var account = await trader.QueryTradingAccountAsync(brokerId, userId, cts.Token);
        Console.WriteLine($"[Trader] 资金: Balance={account.Balance} Available={account.Available}");

        // 查持仓
        Console.WriteLine("[Trader] QueryInvestorPosition…");
        var positions = await trader.QueryInvestorPositionAsync(brokerId, userId, ct: cts.Token);
        Console.WriteLine($"[Trader] 持仓 {positions.Count} 条");

        // 挂单（SimNow 上挂一单，然后撤掉）
        Console.WriteLine("[Trader] OrderInsert（ag2512 限价买 1@8000——远高于市价，不会成交）…");
        var insertResult = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = userId,
            BrokerId = brokerId,
            InstrumentId = "ag2512",
            Direction = Direction.Buy,
            OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit,
            LimitPrice = 8000,
            Volume = 1,
        }, cts.Token);
        Console.WriteLine($"[Trader] 报单: OrderRef={insertResult.OrderRef}");

        await Task.Delay(2000, cts.Token);

        // 撤单
        Console.WriteLine("[Trader] OrderAction（撤单）…");
        await trader.OrderActionAsync(brokerId, userId, loginResult.FrontID, loginResult.SessionID,
            insertResult.OrderRef, "SHFE", cts.Token);
        Console.WriteLine("[Trader] 撤单已发");

        await Task.Delay(2000, cts.Token);
    }
    catch (OperationCanceledException) { Console.WriteLine("[Trader] 超时/取消"); }
    catch (Exception ex) { Console.WriteLine($"[Trader] 异常（协议字节已被代理捕获）: {ex.Message}"); }
}, cts.Token);

// —— 行情腿：连接 → 订阅 → 收 tick ——
var mdTask = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("\n[MD] 启动（经代理 127.0.0.1:17002）…");
        using var md = new CtpMarketData(flowDir);
        md.FrontConnected += (_, _) => Console.WriteLine("[MD] 前置已连接");
        md.TickReceived += (_, tick) => Console.WriteLine($"[MD] Tick: {tick.InstrumentId} last={tick.LastPrice}");
        // 等交易腿先跑几秒
        await Task.Delay(3000, cts.Token);

        Console.WriteLine("[MD] LoginAsync…");
        await md.LoginAsync("tcp://127.0.0.1:17002", brokerId, userId, password, cts.Token);
        Console.WriteLine("[MD] 登录成功");

        Console.WriteLine("[MD] SubscribeMarketData(ag2512)…");
        await md.SubscribeMarketDataAsync(new[] { "ag2512" }, cts.Token);
        Console.WriteLine("[MD] 订阅已发");
    }
    catch (OperationCanceledException) { Console.WriteLine("[MD] 超时/取消"); }
    catch (Exception ex) { Console.WriteLine($"[MD] 异常（协议字节已被代理捕获）: {ex.Message}"); }
}, cts.Token);

// 3. 等会话结束
try { await Task.WhenAll(tradeTask, mdTask); } catch { }
await Task.Delay(2000);   // 尾部心跳

// 4. 停代理 + 输出摘要
tradeProxy.Dispose();
mdProxy.Dispose();

Console.WriteLine($"\n{new string('=', 70)}");
Console.WriteLine($"[Capture] 完成。输出目录: {outputDir}");
foreach (var file in Directory.GetFiles(outputDir, "*.txt"))
{
    var lines = await File.ReadAllLinesAsync(file);
    Console.WriteLine($"\n  {Path.GetFileName(file)}: {lines.Length} 帧");
    Console.WriteLine($"  前 10 帧:");
    foreach (var line in lines.Take(10))
        Console.WriteLine($"    {line}");
    if (lines.Length > 10)
    {
        Console.WriteLine($"  …（省略 {lines.Length - 10} 帧）");
        Console.WriteLine($"  末 5 帧:");
        foreach (var line in lines.Skip(Math.Max(0, lines.Length - 5)))
            Console.WriteLine($"    {line}");
    }
}

// —— 简易 CLI 参数解析 ——
static string? Args(Func<string, bool> match, int skip)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length; i++)
    {
        if (match(args[i]) && i + skip < args.Length)
            return args[i + skip];
    }
    return null;
}
