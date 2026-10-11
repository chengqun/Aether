using System.Globalization;
using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Infrastructure.Catalog;
using Acme.Notes.Infrastructure.DependencyInjection;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Runtime.Lean;
using Acme.Trading.Runtime.Lean.DependencyInjection;
using Acme.Trading.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// CTP console 客户端——登录 / 交易 / 行情 / 策略（Lean 实盘引擎，含 bot），
// 全部复用桌面端同款组件（ICtpSession 官方 SDK 链路 + ILiveTradingRuntime），
// 无 GUI 依赖：SSH 挂机跑 bot / 服务器无人值守 / 轻量手动交易。
//
// 用法（配置档案——推荐）：前置/凭据存 ~/.aether/ctp-console/config.json 的档案（首次启动生成模板），
//   之后只需 --profile <名>；命令行参数逐项覆盖档案。
//   dotnet run --project apps/CtpConsole/Acme.Ctp.Console -- --profile botmm
//   （也可全命令行：--front tcp://x:17801 --md-front tcp://x:17802 --user xx --pass xx）
//
// **完全独立**（2026-10-11 用户定调）：配置 / labs / ctp.db / 编译缓存全在 ~/.aether/ctp-console 下，
// 不与桌面端共享任何东西。算法笔记（leanalgorithms/*.lean.cs）拷一份到自己的 labs 目录。
//
// REPL 命令（help 可打印）：
//   sub <合约>...                          订阅行情（tick 实时打印）
//   unsub                                  退订全部行情
//   tick on|off                            tick 打印开关（bot 挂机时建议 off）
//   account / positions [合约] / orders [合约] / instruments
//   buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>    下单（开/平/平今/平昨）
//   cancel <OrderRef>                      撤单
//   strategy list                           列出已编译策略（两种引擎）
//   strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N]
//                                [--resolution Tick|Second|Minute|Hour|Daily]
//                                [--param k=v]... [--verbose] [--cash 1000000]
//   strategy stop / status
//   quit
// ═══════════════════════════════════════════════════════════════════════════

// —— 配置文件 + 档案解析（命令行 > 配置档案 > 内置默认）——
// console 完全独立（用户定调 2026-10-11）：配置/labs/数据库全部在 ~/.aether/ctp-console 下，
// 不与桌面端共享任何东西。配置文件 ~/<data>/config.json 存多套登录档案（--profile 选择）。
var args2 = new Args(args);
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDir = args2.Get("data") ?? Path.Combine(home, ".aether", "ctp-console");
var configPath = args2.Get("config") ?? Path.Combine(dataDir, "config.json");

ConsoleConfig config;
if (File.Exists(configPath))
{
    config = ConsoleConfig.Load(configPath);
}
else
{
    // 首次启动：生成模板（占位凭据）——填好后重跑，或继续用命令行参数
    config = ConsoleConfig.Template();
    config.Save(configPath);
    Console.WriteLine($"已生成配置模板：{configPath}（填好 front/user/password 后用 --profile 登录；命令行参数可直接覆盖）");
}

// 档案选择：--profile 显式 > defaultProfile > 唯一档案；命令行参数逐项覆盖档案
var profileName = args2.Get("profile") ?? config.DefaultProfile;
ConsoleProfile? profile = null;
if (!string.IsNullOrWhiteSpace(profileName))
{
    if (!config.Profiles.TryGetValue(profileName, out profile))
        throw new ArgumentException($"配置档案 {profileName} 不存在（{configPath} 的 profiles 段：{string.Join(", ", config.Profiles.Keys)}）");
}
else if (config.Profiles.Count == 1)
{
    profile = config.Profiles.Values.First();
}

var front = args2.Get("front") ?? profile?.Front ?? throw new ArgumentException("缺少 --front <交易前置>（或配置档案的 front）");
var mdFront = args2.Get("md-front") ?? profile?.MdFront ?? "";
var user = args2.Get("user") ?? profile?.User ?? throw new ArgumentException("缺少 --user <账户>（或配置档案的 user）");
var password = args2.Get("pass") ?? profile?.Password ?? throw new ArgumentException("缺少 --pass <密码>（或配置档案的 password）");
var broker = args2.Get("broker") ?? profile?.Broker ?? "9999";
var appId = args2.Get("app") ?? profile?.AppId ?? "simnow_client_test";
var authCode = args2.Get("auth") ?? profile?.AuthCode ?? "0000000000000000";
if (!string.IsNullOrWhiteSpace(profileName)) Console.WriteLine($"档案：{profileName}");

// labs 完全独立（console 自己的目录——不读桌面端笔记）：命令行 > 配置 > ~/.aether/ctp-console/labs。
// 算法/策略笔记放这里（leanalgorithms/*.lean.cs / coderules/*.coderule.cs / strategies/…）。
var labsRoot = args2.Get("labs") ?? config.Labs ?? Path.Combine(dataDir, "labs");
Directory.CreateDirectory(dataDir);
if (!Directory.Exists(labsRoot))
    Console.WriteLine($"⚠ labs 目录不存在：{labsRoot}（策略/算法加载为空）——把笔记放进来或 --labs 指定");

// —— DI 组装（全部现成组件；与 Stockapp App.axaml.cs 同款顺序）——
var services = new ServiceCollection();
services.AddCtpInfrastructure(sqliteDbPath: Path.Combine(dataDir, "ctp.db"));
services.AddNotesInfrastructure();          // labs 目录源 + 笔记解析器
services.AddTradingRuntimeLean();           // ILiveTradingRuntime → LeanLiveTradingService（consumes ICtpSession）
services.AddTradingScripting();           // IStrategy 壳引擎的脚本加载（labs → StrategyRegistry）
services.AddLeanAlgorithmServices(dataDir); // Lean 算法脚本编译（labs → LeanAlgorithmRegistry）
services.AddSingleton<INoteCatalogSource>(_ => new DirectoryNoteCatalogSource(() => labsRoot));
services.AddSingleton<IStrategyNoteSource, StrategyNoteSource>();
services.AddSingleton<ILeanAlgorithmNoteSource, LeanAlgorithmNoteSource>();
services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.None)); // 引擎 ILogger → stdout
await using var sp = services.BuildServiceProvider();

var session = sp.GetRequiredService<ICtpSession>();
var live = sp.GetRequiredService<ILiveTradingRuntime>();
var scripting = sp.GetRequiredService<IStrategyScriptingService>();
var leanScripting = sp.GetRequiredService<ILeanAlgorithmScriptingService>();

// —— 事件 → console 打印 ——
var tickOn = true;
var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
session.ConnectionStateChanged += (_, s) => Console.WriteLine($"[连接] {s}");
session.OrderReceived += (_, o) => Console.WriteLine($"[报单] {o}");
session.TradeReceived += (_, t) => Console.WriteLine($"[成交] {t}");
session.TickReceived += (_, t) =>
{
    if (tickOn)
        Console.WriteLine($"[tick] {t.InstrumentId} last={Fmt(t.LastPrice)} bid={Fmt(t.BidPrice)}/{t.BidVolume} ask={Fmt(t.AskPrice)}/{t.AskVolume} vol={t.Volume} {t.UpdateTime}.{t.UpdateMillisec:000}");
};
live.LogReceived += (_, line) => Console.WriteLine(line);
live.StatusChanged += (_, s) => Console.WriteLine($"[实盘] 状态: {s}");

// —— 登录 ——
Console.WriteLine($"登录中：{front} 账户 {user} …");
var login = await session.LoginAsync(new CtpLoginRequest
{
    FrontAddress = front,
    MdFrontAddress = mdFront,
    BrokerId = broker,
    UserId = user,
    Password = password,
    AppId = appId,
    AuthCode = authCode,
});
Console.WriteLine($"已登录：交易日 {login.TradingDay} FrontId={login.FrontId} SessionId={login.SessionId} MaxOrderRef={login.MaxOrderRef}");

// —— 策略/算法笔记加载（labs → 编译 → 注册表；console 生命周期内加载一次，改笔记重跑）——
await scripting.LoadAsync();
await leanScripting.LoadAsync();
PrintStrategies();

// —— REPL ——
Console.WriteLine("输入 help 查看命令。");
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null) break;   // EOF（Ctrl+D / 管道结束）
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) continue;
    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "quit" or "exit":
                await ShutdownAsync();
                return;
            case "help":
                PrintHelp();
                break;
            case "sub":
                await SubscribeAsync(parts[1..]);
                break;
            case "unsub":
                session.UnsubscribeMarketData();
                subscribed.Clear();
                Console.WriteLine("已退订全部行情");
                break;
            case "tick":
                tickOn = parts.Length < 2 || !string.Equals(parts[1], "off", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"tick 打印 {(tickOn ? "开" : "关")}");
                break;
            case "account":
                Console.WriteLine(await session.QueryAccountAsync());
                break;
            case "positions" or "pos":
                PrintList(await session.QueryPositionAsync(parts.Length > 1 ? parts[1] : null));
                break;
            case "orders":
                PrintList(await session.QueryOrderAsync(parts.Length > 1 ? parts[1] : null));
                break;
            case "instruments":
                PrintList(await session.QueryInstrumentsAsync());
                break;
            case "buy" or "sell":
                await OrderAsync(parts);
                break;
            case "cancel":
                await CancelAsync(parts);
                break;
            case "strategy":
                await StrategyAsync(parts);
                break;
            case "status":
                Console.WriteLine($"引擎 {live.Status}（运行中={live.IsRunning}）tick 打印={(tickOn ? "开" : "关")}");
                break;
            default:
                Console.WriteLine($"未知命令 {parts[0]}（help 查看命令）");
                break;
        }
    }
    catch (TimeoutException)
    {
        // 已知边界（CLAUDE.md CTP 前置节）：查报单空表时 SDK 不派发 17B tag 空应答 ⇒ 30s 超时后管线自动恢复
        //（真前置首查过滤时同样超时——录制实证）。查询超时不影响后续命令。
        Console.WriteLine("[超时] 空表查询 SDK 不派发应答（已知边界——委托/持仓空表时可能发生），管线已恢复，可继续。");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[错误] {ex.Message}");
    }
}

// —— 命令实现 ——

async Task SubscribeAsync(string[] symbols)
{
    if (symbols.Length == 0) { Console.WriteLine("用法：sub <合约> <合约>…"); return; }
    if (mdFront.Length == 0) { Console.WriteLine("缺少行情前置（启动参数 --md-front tcp://ip:port）"); return; }
    await session.SubscribeMarketDataAsync(new CtpMarketDataConnect
    {
        MdFrontAddress = mdFront, BrokerId = broker, UserId = user, Password = password,
    }, symbols);
    foreach (var s in symbols) subscribed.Add(s);
    Console.WriteLine($"已订阅：{string.Join(' ', symbols)}");
}

async Task OrderAsync(string[] parts)
{
    // buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>
    if (parts.Length != 6) { Console.WriteLine("用法：buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>"); return; }
    var direction = parts[0].Equals("buy", StringComparison.OrdinalIgnoreCase) ? CtpDirection.Buy : CtpDirection.Sell;
    var offset = parts[1].ToLowerInvariant() switch
    {
        "o" => CtpOffsetFlag.Open,
        "c" => CtpOffsetFlag.Close,
        "ct" => CtpOffsetFlag.CloseToday,
        "cy" => CtpOffsetFlag.CloseYesterday,
        _ => throw new ArgumentException($"开平非法：{parts[1]}（o=开 c=平 ct=平今 cy=平昨）"),
    };
    var isMarket = parts[3].Equals("market", StringComparison.OrdinalIgnoreCase);
    var price = isMarket ? 0 : ParseDouble(parts[3]);
    var volume = int.Parse(parts[4], CultureInfo.InvariantCulture);
    var result = await session.OrderInsertAsync(new CtpOrderRequest
    {
        BrokerId = broker, InvestorId = user, InstrumentId = parts[2],
        Direction = direction, OffsetFlag = offset,
        PriceType = isMarket ? CtpOrderPriceType.Market : CtpOrderPriceType.Limit,
        LimitPrice = price, Volume = volume,
    });
    Console.WriteLine($"已报单 ref={result.OrderRef} reqId={result.RequestId}（回报异步推送——orders 查状态）");
}

async Task CancelAsync(string[] parts)
{
    if (parts.Length != 2) { Console.WriteLine("用法：cancel <OrderRef>"); return; }
    var loginResult = session.LoginResult ?? throw new InvalidOperationException("未登录");
    await session.OrderActionAsync(new CtpOrderRef(broker, user, loginResult.FrontId, loginResult.SessionId, parts[1], ""));
    Console.WriteLine("撤单请求已发（结果经报单回报推送）");
}

async Task StrategyAsync(string[] parts)
{
    if (parts.Length < 2) { Console.WriteLine("用法：strategy list | run <名> <合约>... [选项] | stop"); return; }
    switch (parts[1].ToLowerInvariant())
    {
        case "list":
            PrintStrategies();
            break;
        case "stop":
            Console.WriteLine("请求停止…");
            await live.StopAsync();
            break;
        case "run":
            await StrategyRunAsync(parts[2..]);
            break;
        default:
            Console.WriteLine($"未知子命令 {parts[1]}（list / run / stop）");
            break;
    }
}

async Task StrategyRunAsync(string[] parts)
{
    if (parts.Length < 2) { Console.WriteLine("用法：strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N] [--param k=v]..."); return; }
    var name = parts[0];
    var engine = LiveStrategyEngine.LeanAlgorithm;   // console 主场景 = bot（Lean 算法）
    var instruments = new List<string>();
    var resolution = BarResolution.Tick;              // bot 默认 Tick（桌面实盘 tab 同款推荐）
    var real = false;
    var maxVolume = 1;
    var verbose = false;
    var cash = 1_000_000m;
    var parameters = new Dictionary<string, string>();
    for (var i = 1; i < parts.Length; i++)
    {
        switch (parts[i])
        {
            case "--engine":
                engine = parts[++i].Equals("strategy", StringComparison.OrdinalIgnoreCase) ? LiveStrategyEngine.IStrategy : LiveStrategyEngine.LeanAlgorithm;
                break;
            case "--real":
                real = true;
                break;
            case "--max-volume":
                maxVolume = int.Parse(parts[++i], CultureInfo.InvariantCulture);
                break;
            case "--resolution":
                resolution = parts[++i] switch
                {
                    "Tick" => BarResolution.Tick, "Second" => BarResolution.Second,
                    "Minute" => BarResolution.Minute, "Hour" => BarResolution.Hour,
                    "Daily" => BarResolution.Daily,
                    _ => throw new ArgumentException($"分辨率非法：{parts[i]}"),
                };
                break;
            case "--verbose":
                verbose = true;
                break;
            case "--cash":
                cash = ParseDecimal(parts[++i]);
                break;
            case "--param":
            {
                var kv = parts[++i].Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"--param 须 k=v：{parts[i]}");
                parameters[kv[0]] = kv[1];
                break;
            }
            default:
                instruments.Add(parts[i]);
                break;
        }
    }
    if (instruments.Count == 0) { Console.WriteLine("至少一个合约（strategy run <名> <合约>…）"); return; }
    if (mdFront.Length == 0) { Console.WriteLine("缺少行情前置（启动参数 --md-front tcp://ip:port）"); return; }

    Console.WriteLine($"[实盘] 启动：合约={string.Join(",", instruments)} 引擎={engine} 策略={name} 分辨率={resolution} " +
                      $"真实下单={(real ? $"开（单笔开仓上限 {maxVolume} 手）" : "关（仅信号）")}");
    var (ok, err) = await live.StartAsync(new LiveTradingRuntimeRequest
    {
        Instruments = instruments,
        Engine = engine,
        StrategyName = name,
        InitialCash = cash,
        StrategyParameters = parameters.Count > 0 ? parameters : null,
        Resolution = resolution,
        VerboseLog = verbose,
        EnableRealOrders = real,
        MaxOrderVolume = maxVolume,
        MarketDataConnect = new MarketDataConnectInput
        {
            MdFrontAddress = mdFront, BrokerId = broker, UserId = user, Password = password,
        },
    });
    if (!ok) Console.WriteLine($"[实盘] 启动失败：{err}");
}

async Task ShutdownAsync()
{
    if (live.IsRunning)
    {
        Console.WriteLine("停止策略引擎…");
        try { await live.StopAsync(); } catch { }
    }
    session.Logout();
    Console.WriteLine("已退出。");
}

void PrintStrategies()
{
    var lean = LeanAlgorithmRegistry.GetRegisteredNames();
    Console.WriteLine(lean.Count > 0
        ? $"Lean 算法（--engine lean）：{string.Join(", ", lean.OrderBy(n => n, StringComparer.Ordinal))}"
        : "Lean 算法：无（labs leanalgorithms 目录为空或编译失败）");
    var strats = StrategyRegistry.GetRegisteredNames();
    Console.WriteLine(strats.Count > 0
        ? $"IStrategy（--engine strategy）：{string.Join(", ", strats.OrderBy(n => n, StringComparer.Ordinal))}"
        : "IStrategy：无（labs coderules/strategies 目录为空或编译失败）");
}

static void PrintList<T>(IReadOnlyList<T> list)
{
    if (list.Count == 0) { Console.WriteLine("（空）"); return; }
    foreach (var item in list) Console.WriteLine(item);
}

static void PrintHelp()
{
    Console.WriteLine("""
        sub <合约>...            订阅行情（tick 实时打印）
        unsub                    退订全部
        tick on|off              tick 打印开关（bot 挂机建议 off）
        account                  查资金
        positions [合约]          查持仓
        orders [合约]            查委托
        instruments              查合约表（sim 上市表）
        buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>   下单（开/平/平今/平昨）
        cancel <OrderRef>        撤单
        strategy list            列出已编译策略
        strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N]
                                  [--resolution Tick|Second|Minute|Hour|Daily] [--param k=v]... [--verbose] [--cash N]
        strategy stop            停止引擎
        status                   引擎状态
        quit                     退出
        """);
}

static double ParseDouble(string s) => double.Parse(s, CultureInfo.InvariantCulture);
static decimal ParseDecimal(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

static string Fmt(double v) => v == 0 ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);

/// <summary>极简 --key value 参数表（与 apps/LedgerWallet 的 Args 同款模式）。</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            var value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
            _map[key] = value;
        }
    }
    public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
}
