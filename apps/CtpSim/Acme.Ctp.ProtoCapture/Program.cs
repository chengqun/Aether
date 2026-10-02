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
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen [trade.txt ...] [输出.cs]
//   （抓包可传多个：第 1 个 = M1 模板源（031656 固定基准）；全部抓包合并供 M2 模板按 op 定位）
var directArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (directArgs.Length > 0 && directArgs[0] == "gen")
{
    FrontGen.Run(directArgs.Skip(1).ToArray());
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
var durationSec = int.TryParse(Args(arg => arg == "--duration", 1), out var d) ? d : 100;   // M2：默认拉长——五场景 + 40s idle 心跳观测

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

// 2. 用官方 SDK 走代理跑一轮会话（M2 抓包：五场景——故意拒单兜底 / 成交 / 挂单 / 撤单 / idle 心跳观测）
var flowDir = Path.Combine(outputDir, "flow");
Directory.CreateDirectory(flowDir);

var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));
var exchangeId = Args(arg => arg == "--exchange", 1) ?? "DCE";
var instrument = Args(arg => arg == "--instrument", 1) ?? "m2701";   // 2026-10 当前有效合约（抓包 v1 实证 ag2512 已过期→ErrorID 16 找不到合约；m2701/IF2612 有真实行情）

// 首个 tick 共享源（行情腿 set，交易腿等——成交场景要动态定价）
var firstTick = new TaskCompletionSource<TickData>(TaskCreationOptions.RunContinuationsAsynchronously);

// —— 交易腿：登录 → 查询 → ①故意拒单（无 ExchangeID，确定性复现完整 ⑳）→ ②激进限价（成交）
//    → ③远端限价（挂单）→ ④撤单 → ⑤剩余时间 idle（捕服务端心跳）——
var tradeTask = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("\n[Trader] 启动（经代理 127.0.0.1:17001）…");
        using var trader = new CtpTrader(flowDir);
        trader.FrontConnected += (_, _) => Console.WriteLine("[Trader] 前置已连接");
        trader.FrontDisconnected += (_, reason) => Console.WriteLine($"[Trader] 前置断开: {reason}");
        trader.RspError += (_, e) => Console.WriteLine($"[Trader] RspError: [{e.ErrorId}] {e.ErrorMsg}");
        trader.OrderReceived += (_, o) => Console.WriteLine($"[Trader] OnRtnOrder: {o.InstrumentId} ref={o.OrderRef} status={(char)o.OrderStatusRaw} traded={o.VolumeTraded}/{o.VolumeTotalOriginal} price={o.LimitPrice}");
        trader.TradeReceived += (_, t) => Console.WriteLine($"[Trader] OnRtnTrade: {t.InstrumentId} ref={t.OrderRef} {t.Volume}@{t.Price} id={t.TradeId}");

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
        Console.WriteLine($"[Trader] 登录成功: 交易日={loginResult.TradingDay} FrontID={loginResult.FrontID} SessionID={loginResult.SessionID} MaxOrderRef={loginResult.MaxOrderRef}");

        Console.WriteLine("[Trader] ConfirmSettlement…");
        await trader.ConfirmSettlementAsync(brokerId, userId, cts.Token);
        Console.WriteLine("[Trader] 结算确认完成");

        Console.WriteLine("[Trader] QueryTradingAccount…");
        var account = await trader.QueryTradingAccountAsync(brokerId, userId, cts.Token);
        Console.WriteLine($"[Trader] 资金: Balance={account.Balance} Available={account.Available}");

        Console.WriteLine("[Trader] QueryInvestorPosition…");
        var positions = await trader.QueryInvestorPositionAsync(brokerId, userId, ct: cts.Token);
        Console.WriteLine($"[Trader] 持仓 {positions.Count} 条");

        // 查合约（M3 板子数据源的 wire op 抓包——SDK 自己构造请求帧，应答帧 = 模板素材）
        Console.WriteLine("[Trader] QueryInstruments…");
        var instruments = await trader.QueryInstrumentsAsync(cts.Token);
        Console.WriteLine($"[Trader] 合约 {instruments.Count} 个（首 5: {string.Join(",", instruments.Take(5).Select(i => i.InstrumentId))}）");

        // 等首个 tick（成交场景的动态定价基准；无行情则回退固定价——成交场景可能落空）
        TickData? tick = null;
        try { tick = await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(12), cts.Token); }
        catch (TimeoutException) { Console.WriteLine("[Trader] 12s 无 tick——回退固定价基准（成交场景可能落空）"); }
        var aggressive = tick is { AskPrice: > 0 } ? tick.AskPrice + 50 : 3400;   // 回退价 = m2701 量级（~3366，抓包 v1 实证）
        var resting = tick is { LastPrice: > 0 } ? Math.Min(tick.LastPrice, tick.BidPrice > 0 ? tick.BidPrice : tick.LastPrice) - 50 : 3300;
        Console.WriteLine($"[Trader] 定价基准: aggressive={aggressive} resting={resting}（tick? {(tick is null ? "无" : $"{tick.InstrumentId} last={tick.LastPrice}")}）");

        // ── 场景①：无 ExchangeID 故意拒单——确定性复现完整 ⑳（兜底：不依赖 SimNow 接受修复后的单）──
        Console.WriteLine($"[Trader] 场景① 报单（无 ExchangeID，预期 ⑳ 拒单 + F003 ErrRtn）: {instrument} 限价买 1@{resting}…");
        var rej = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = userId, BrokerId = brokerId, InstrumentId = instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = resting, Volume = 1,
            ExchangeId = "",   // ← 故意留空
        }, cts.Token);
        Console.WriteLine($"[Trader] 场景① 报单已发 ref={rej.OrderRef}");
        await Task.Delay(3000, cts.Token);   // 等 ⑳ 两子帧 + F003 全到达

        // ── 场景②：激进限价（带 ExchangeID，预期成交——OnRtnOrder '0' + OnRtnTrade）──
        Console.WriteLine($"[Trader] 场景② 报单（激进限价 {aggressive}，预期成交）…");
        var fill = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = userId, BrokerId = brokerId, InstrumentId = instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = aggressive, Volume = 1,
            ExchangeId = exchangeId,
        }, cts.Token);
        Console.WriteLine($"[Trader] 场景② 报单已发 ref={fill.OrderRef}");
        await Task.Delay(5000, cts.Token);   // 等 OnRtnOrder/OnRtnTrade 推送

        // ── 场景③：远端限价挂单（预期 OnRtnOrder '3'，静默不成交）──
        Console.WriteLine($"[Trader] 场景③ 报单（远端限价 {resting}，预期挂单）…");
        var rest = await trader.OrderInsertAsync(new OrderRequest
        {
            InvestorId = userId, BrokerId = brokerId, InstrumentId = instrument,
            Direction = Direction.Buy, OffsetFlag = OffsetFlag.Open,
            PriceType = OrderPriceType.Limit, LimitPrice = resting, Volume = 1,
            ExchangeId = exchangeId,
        }, cts.Token);
        Console.WriteLine($"[Trader] 场景③ 报单已发 ref={rest.OrderRef}");
        await Task.Delay(3000, cts.Token);

        // ── 场景④：撤场景③的单（预期成功 ㉒ + OnRtnOrder '5'）──
        Console.WriteLine("[Trader] 场景④ OrderAction（撤场景③挂单，预期成功 ㉒）…");
        await trader.OrderActionAsync(brokerId, userId, loginResult.FrontID, loginResult.SessionID,
            rest.OrderRef, exchangeId, cts.Token);
        Console.WriteLine("[Trader] 场景④ 撤单应答已收（OnRspOrderAction 正常返回）");
        await Task.Delay(3000, cts.Token);

        // ── 场景⑤：idle——捕服务端心跳（若有）。剩余时长由 --duration 控制（默认足够 40s+）──
        Console.WriteLine($"[Trader] 场景⑤ idle {Math.Max(40, durationSec - 40)}s（观测心跳/断连）…");
        try { await Task.Delay(Math.Max(40, durationSec - 40) * 1000, cts.Token); }
        catch (OperationCanceledException) { }
        Console.WriteLine("[Trader] idle 结束（连接仍活？见 FrontDisconnected 日志）");
    }
    catch (OperationCanceledException) { Console.WriteLine("[Trader] 超时/取消"); }
    catch (Exception ex) { Console.WriteLine($"[Trader] 异常（协议字节已被代理捕获）: {ex.Message}"); }
}, cts.Token);

// —— 行情腿：连接 → 订阅 → 收 tick（多合约订阅：节假日哪只有行情用哪只）——
var mdTask = Task.Run(async () =>
{
    try
    {
        Console.WriteLine("\n[MD] 启动（经代理 127.0.0.1:17002）…");
        using var md = new CtpMarketData(flowDir);
        md.FrontConnected += (_, _) => Console.WriteLine("[MD] 前置已连接");
        md.TickReceived += (_, tick) =>
        {
            Console.WriteLine($"[MD] Tick: {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice} ask={tick.AskPrice}");
            firstTick.TrySetResult(tick);
        };
        await Task.Delay(1000, cts.Token);

        Console.WriteLine("[MD] LoginAsync…");
        await md.LoginAsync("tcp://127.0.0.1:17002", brokerId, userId, password, cts.Token);
        Console.WriteLine("[MD] 登录成功");

        Console.WriteLine($"[MD] SubscribeMarketData({instrument} 及当前有效合约)…");
        await md.SubscribeMarketDataAsync(new[] { instrument, "i2701", "ta2701", "IF2612" }.Distinct().ToArray(), cts.Token);   // 勿混过期合约（v1 实证 2512 系全过期，疑似致 MD 断连）
        Console.WriteLine("[MD] 订阅已发");
        // 长会话保持存活（v2 教训：订阅即 dispose ⇒ 从未观察过真实 tick 流的到达节奏与帧形状——
        // 这是本地前置 tick 重投 bug 缺的地面真相）。存活至会话结束，逐帧记录。
        Console.WriteLine("[MD] 保持存活收 tick 流…");
        try { await Task.Delay(TimeSpan.FromSeconds(85), cts.Token); }
        catch (OperationCanceledException) { }
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
