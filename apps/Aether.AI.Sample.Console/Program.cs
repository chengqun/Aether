using Acme.Ctp.Domain;
using Acme.Ctp.Application;
using Acme.Ctp.AI;
using Acme.Ctp.Application.DependencyInjection;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.SystemTools.AI;
using Acme.ApkParser.AI;
using Acme.Notes.AI;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.Infrastructure.DependencyInjection;
using Ctp.Net.Core.Trader;
using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;
using Acme.Llm.Application;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Application;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Application.DependencyInjection;
using Acme.AppAuth.Infrastructure;
using Acme.AppAuth.Infrastructure.DependencyInjection;
using Acme.Qifu.AI;
using Acme.Qifu.Application;
using Acme.Qifu.Application.DependencyInjection;
using Acme.Qifu.Infrastructure;
using Acme.Qifu.Infrastructure.DependencyInjection;
using Acme.Quotes.AI;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Infrastructure.DependencyInjection;
using Acme.Securities.Application;
using Acme.Securities.Application.DependencyInjection;
using Acme.Securities.Infrastructure.DependencyInjection;
using Acme.Trading.AI;
using Acme.Trading.Application;
using Acme.Trading.Application.DependencyInjection;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Runtime.Lean.DependencyInjection;
using Aether.AI.Hosting;
using Aether.AI.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ---- Aether.AI integrated Console demo -----------------------------------
// Composes every context: Platform (app auth/HTTP) + Llm (LLM config) + Qifu +
// Ctp + Securities + Quotes + Trading + Notes + Aether.AI. One assistant agent
// aggregates all tools. 'sync-securities' pulls instruments
// from the app server; 'use <agent>' switches the active agent.

var dbPath = Environment.GetEnvironmentVariable("LLM_DB_PATH")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "llm.db");

// --- 1. LLM config in SQLite: read the active config; enter/seed if absent ---
var llmRepo = SqliteLlmConfigRepository.ForFile(dbPath);
var all = await llmRepo.GetAllAsync();
LlmConfig? active = all.FirstOrDefault(c => c.IsDefault) ?? all.FirstOrDefault();
var forceNewConfig = args.Contains("--config", StringComparer.OrdinalIgnoreCase);

if (forceNewConfig || active is null)
{
    var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!forceNewConfig && !string.IsNullOrWhiteSpace(envKey))
    {
        active = new LlmConfig
        {
            Id = Guid.NewGuid(),
            Name = "console-default",
            ModelName = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "deepseek-chat",
            ApiKey = envKey,
            Endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "https://api.deepseek.com",
            Temperature = 0.5f,
            IsDefault = true
        };
        await SaveDefaultConfigAsync(llmRepo, active);
        Console.WriteLine($"Seeded LLM config '{active.Name}' ({active.ModelName} @ {active.Endpoint}) into {dbPath}");
    }
    else
    {
        Console.WriteLine(active is null
            ? "No LLM config found in SQLite. Let's create one."
            : "'--config': enter a new LLM config (becomes the default).");
        var dto = PromptLlmConfigDto();
        active = new LlmConfig
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ModelName = dto.ModelName,
            ApiKey = dto.ApiKey,
            Endpoint = dto.Endpoint,
            Temperature = dto.Temperature,
            IsDefault = true
        };
        await SaveDefaultConfigAsync(llmRepo, active);
        Console.WriteLine($"Saved LLM config '{active.Name}' as default.");
    }
}

if (string.IsNullOrWhiteSpace(active.ApiKey))
{
    await RunOfflineSmokeAsync(dbPath);
    return 0;
}

Console.WriteLine($"Using LLM config from SQLite: {active.Name} — {active.ModelName} @ {active.Endpoint}");
Console.WriteLine(new string('-', 60));

// --- 2. Read LLM config from SQLite → assemble into AetherAIOptions ---
var builder = Host.CreateApplicationBuilder(args);
var aether = builder.Services.AddAetherAI();
aether.Configure(opts =>
{
    opts.OpenAI.ApiKey = active.ApiKey;
    opts.OpenAI.ModelId = active.ModelName;
    opts.OpenAI.Endpoint = active.Endpoint;
});

// --- 3. Compose all contexts + agents ---
ComposeContexts(builder.Services);
RegisterAgents(aether, active.ModelName);

var app = builder.Build();
var runtime = app.Services.GetRequiredService<HarnessRuntime>();

await RunConversationAsync(runtime, app.Services);
return 0;

// ---------------------------------------------------------------------------

static void ComposeContexts(IServiceCollection services)
{
    // Platform: app auth + authenticated HTTP + current user.
    // Defaults mirror AlphaAgent's stockapp (overridable via APP_BASEURL / APP_CLIENT_ID /
    // APP_CLIENT_SECRET / APP_TENANT env vars).
    services.AddAppAuthInfrastructure(o =>
    {
        o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL") ?? "https://117.72.186.128/";
        o.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? "alphaagent_chat";
        o.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? "chat_secret";
        o.Tenant = Environment.GetEnvironmentVariable("APP_TENANT") ?? "stockapp";
    });
    services.AddAppAuthApplication();
    // Llm: LLM config in SQLite.
    services.AddLlmInfrastructure();
    services.AddLlmApplication();
    // Qifu: 360 internal-platform integration.
    services.AddQifuInfrastructure();
    services.AddQifuApplication();
    // Ctp: futures broker session (self-contained, native CTP binding).
    services.AddCtpInfrastructure();
    services.AddCtpApplication();
    // Securities: instruments master data + server sync.
    services.AddSecurityInfrastructure();
    services.AddSecurityApplication();
    // Quotes: market data + indicators + analysis (ACL-bridged to Securities via ai/Quotes).
    services.AddQuotesInfrastructure();
    services.AddQuotesApplication();
    // Trading: backtest + live trading runtime backed by in-process Lean engine.
    // Consumes ICtpSession (Ctp) + IAnalysisManager (Quotes); Lean source confined to Runtime.Lean.
    services.AddTradingApplication();
    services.AddTradingRuntimeLean();
    // Notes: local Markdown notes (SQLite tree store).
    services.AddNotesInfrastructure();
    services.AddNotesApplication();
}

static void RegisterAgents(AetherAIBuilder aether, string modelId)
{
    // One agent with all tools from every context (no agent switching needed).
    aether.AddQifuTools().AddCtpTools().AddQuotesTools().AddTradingTools();
    aether.AddSystemTools().AddApkParser().AddNotesTools();
    var allTools = QifuAIBuilderExtensions.ToolNames
        .Concat(CtpAIBuilderExtensions.ToolNames)
        .Concat(QuotesAIBuilderExtensions.ToolNames)
        .Concat(TradingAIBuilderExtensions.ToolNames)
        .Concat(SystemToolsAIBuilderExtensions.ToolNames)
        .Concat(ApkParserAIBuilderExtensions.ToolNames)
        .Concat(NotesAIBuilderExtensions.ToolNames)
        .ToArray();
    aether.AddAgent("assistant", a => a
        .Name("Aether Assistant")
        .Instructions(
            "你是企业助手，可调用工具：证券查询/技术指标、CTP 期货（账户/持仓/行情/下单/撤单）、" +
            "量化回测（SMA 策略历史回测，返回收益/风险统计）、毓数异步 SQL、360Teams 云文档/消息通知。" +
            "按需调用工具，简洁回复。下单/撤单默认仅预览。")
        .ModelId(modelId)
        .WithTools(allTools));
}

static async Task RunOfflineSmokeAsync(string dbPath)
{
    Console.WriteLine($"No LLM config with an API key found in {dbPath}.");
    Console.WriteLine("Set OPENAI_API_KEY (or run interactively) to seed one and run live.");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine("Offline smoke check: building the composed host (no model calls)...");

    var builder = Host.CreateApplicationBuilder();
    var aether = builder.Services.AddAetherAI();
    aether.Configure(o => { o.OpenAI.ApiKey = "unused-offline"; o.OpenAI.ModelId = "offline"; });
    ComposeContexts(builder.Services);
    RegisterAgents(aether, "offline");

    var app = builder.Build();
    var runtime = app.Services.GetRequiredService<HarnessRuntime>();
    Console.WriteLine($"Assistant registered: {runtime.HasAgent("assistant")}");
    var session = runtime.StartSession("assistant");
    Console.WriteLine($"session: {session.SessionId} ({session.AgentName})");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine("Offline smoke passed. Provide an LLM API key to run a live conversation.");
}

static async Task RunConversationAsync(HarnessRuntime runtime, IServiceProvider services)
{
    Console.WriteLine("Aether.AI live. Commands: 'ctp', 'ctp-login', 'ctp-status', 'sync-securities', 'backtest', 'login', 'whoami', 'configs', 'config', 'exit'.");
    Console.WriteLine("One assistant with all tools (qifu/ctp/quotes). Tool backends still need their own login/credentials.");
    Console.WriteLine(new string('-', 60));

    var llmService = services.GetRequiredService<ILlmConfigService>();
    var authService = services.GetRequiredService<IAppAuthService>();
    var currentUser = services.GetRequiredService<ICurrentUser>();
    var securitySync = services.GetRequiredService<ISecurityClientSyncService>();
    var ctpConfigStore = services.GetRequiredService<ICtpConfigStore>();
    var ctpSession = services.GetRequiredService<ICtpTradingService>();
    var backtestService = services.GetRequiredService<IBacktestApplicationService>();

    var session = runtime.StartSession("assistant");

    // Like AlphaAgent's stockapp: on startup, if already logged in (cached app token),
    // kick off a background securities sync from the server (non-blocking).
    await KickoffBackgroundSyncAsync(securitySync, authService, "startup");

    while (true)
    {
        Console.Write("you > ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) continue;
        var cmd = input.Trim();

        if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

        if (cmd.Equals("ctp", StringComparison.OrdinalIgnoreCase))
        {
            await ConfigureCtpAsync(ctpConfigStore);
            continue;
        }
        if (cmd.Equals("ctp-login", StringComparison.OrdinalIgnoreCase))
        {
            await CtpLoginAsync(ctpConfigStore, ctpSession);
            continue;
        }
        if (cmd.Equals("ctp-status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(ctpSession.IsConnected
                ? $"CTP connected (交易日={ctpSession.LoginResult?.TradingDay}, FrontID={ctpSession.LoginResult?.FrontId}, SessionID={ctpSession.LoginResult?.SessionId})"
                : "CTP not connected.");
            continue;
        }

        if (cmd.Equals("sync-securities", StringComparison.OrdinalIgnoreCase))
        {
            await SyncSecuritiesAsync(securitySync);
            continue;
        }

        if (cmd.Equals("backtest", StringComparison.OrdinalIgnoreCase))
        {
            await RunBacktestAsync(backtestService);
            continue;
        }

        if (cmd.Equals("login", StringComparison.OrdinalIgnoreCase))
        {
            if (await LoginAsync(authService, currentUser))
                await KickoffBackgroundSyncAsync(securitySync, authService, "post-login");
            continue;
        }
        if (cmd.Equals("whoami", StringComparison.OrdinalIgnoreCase))
        {
            var uid = await currentUser.GetCurrentUserIdAsync();
            Console.WriteLine(uid is null ? "(not logged in)" : uid);
            continue;
        }
        if (cmd.Equals("configs", StringComparison.OrdinalIgnoreCase)) { await ListConfigsAsync(llmService); continue; }
        if (cmd.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            var dto = PromptLlmConfigDto();
            await llmService.SetConfigAsync(dto);
            Console.WriteLine($"Saved '{dto.Name}' as default. Restart to apply it to the running agent.");
            continue;
        }

        string reply;
        try
        {
            reply = await runtime.RunAsync(session.SessionId, input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[error] {ex.Message}");
            continue;
        }

        Console.WriteLine($"{session.AgentName} > {reply}");
        if (session.ToolCalls.Count > 0)
            Console.WriteLine($"  (tools called: {string.Join(", ", session.ToolCalls.Distinct())})");
    }
}

static async Task SyncSecuritiesAsync(ISecurityClientSyncService securitySync)
{
    try
    {
        var result = await securitySync.SyncFromServerAsync();
        Console.WriteLine($"Synced {result.SyncedCount} securities (full={result.IsFullSync}, last={result.LastSyncTime:O}).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Sync failed: {ex.Message}");
    }
}

/// <summary>
/// Functional test for the Trading backtest path: prompts for params, runs one Lean backtest
/// via IBacktestApplicationService, prints stats or the error. Lean engine blocks, so offload
/// to a background thread. Needs quote data for the symbol — run 'sync-securities' first and
/// ensure the Quotes provider can fetch the contract (e.g. ag2610).
/// </summary>
static async Task RunBacktestAsync(IBacktestApplicationService backtestService)
{
    var symbol = Prompt("Symbol (期货合约, 如 ag2610)", "ag2610");
    var startDate = Prompt("StartDate (yyyy-MM-dd)", "2024-01-01");
    var endDate = Prompt("EndDate (yyyy-MM-dd)", "2024-12-31");
    var strategy = Prompt("Strategy (SmaCross/Test)", "SmaCross");
    var cash = decimal.TryParse(Prompt("InitialCash", "100000"), out var c) ? c : 100_000m;
    var fast = int.TryParse(Prompt("FastPeriod", "10"), out var f) ? f : 10;
    var slow = int.TryParse(Prompt("SlowPeriod", "30"), out var s) ? s : 30;
    var resolutionStr = Prompt("Resolution (Daily/Hour/Minute)", "Daily");
    if (!Enum.TryParse<BarResolution>(resolutionStr, ignoreCase: true, out var resolution))
        resolution = BarResolution.Daily;

    if (!DateTime.TryParse(startDate, out var start) || !DateTime.TryParse(endDate, out var end))
    {
        Console.WriteLine("日期格式无效。");
        return;
    }

    var input = new BacktestRunInput
    {
        Symbol = symbol,
        StrategyName = strategy,
        StartDate = start,
        EndDate = end,
        InitialCash = cash,
        FastPeriod = fast,
        SlowPeriod = slow,
        Resolution = resolution,
    };

    Console.WriteLine($"回测运行中… {symbol} {strategy} {start:yyyy-MM-dd}~{end:yyyy-MM-dd} {resolution} cash={cash} fast={fast} slow={slow}");
    Console.WriteLine(new string('-', 60));

    Acme.Trading.Domain.BacktestResult result;
    try
    {
        // Lean engine.Run 是同步阻塞 CPU 活，卸载到后台线程避免卡住命令循环。
        result = await Task.Run(() => backtestService.RunAsync(input));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"回测异常: {ex.Message}");
        return;
    }

    if (!result.IsSuccess)
    {
        Console.WriteLine($"回测未成功: {result.Error ?? "(无错误信息)"}  状态={result.AlgorithmStatus}");
        if (result.Logs.Count > 0)
        {
            Console.WriteLine("日志（末 20 行）:");
            foreach (var line in result.Logs.TakeLast(20)) Console.WriteLine($"  {line}");
        }
        return;
    }

    var st = result.Statistics;
    Console.WriteLine($"回测成功！状态={result.AlgorithmStatus}  权益点数={result.EquityCurve.Count}");
    Console.WriteLine($"  净收益={st?.NetProfit}  总收益={st?.TotalReturn}  年化={st?.Cagr}");
    Console.WriteLine($"  Sharpe={st?.SharpeRatio}  Sortino={st?.SortinoRatio}  最大回撤={st?.MaxDrawdown}");
    Console.WriteLine($"  胜率={st?.WinRate}  交易次数={st?.TotalTrades}  Alpha={st?.Alpha}  Beta={st?.Beta}");
    if (result.Logs.Count > 0)
    {
        Console.WriteLine("信号日志（末 10 行）:");
        foreach (var line in result.Logs.TakeLast(10)) Console.WriteLine($"  {line}");
    }
}

static async Task ConfigureCtpAsync(ICtpConfigStore store)
{
    var existing = await store.GetAsync();
    Console.WriteLine(existing is null
        ? "No CTP config saved. Enter connection details:"
        : "Enter CTP connection details (press Enter to keep current value):");
    var config = new CtpConnectionConfig
    {
        FrontAddress = Prompt("FrontAddress (trading)", existing?.FrontAddress ?? ""),
        MdFrontAddress = Prompt("MdFrontAddress (market data)", existing?.MdFrontAddress ?? ""),
        BrokerId = Prompt("BrokerId", existing?.BrokerId ?? ""),
        UserId = Prompt("UserId", existing?.UserId ?? ""),
        Password = Prompt("Password", existing?.Password ?? ""),
        AppId = Prompt("AppId", existing?.AppId ?? ""),
        AuthCode = Prompt("AuthCode", existing?.AuthCode ?? "")
    };
    await store.SaveAsync(config);
    Console.WriteLine("CTP config saved to SQLite.");
}

static async Task CtpLoginAsync(ICtpConfigStore store, ICtpTradingService tradingService)
{
    if (tradingService.IsConnected) { Console.WriteLine("Already connected. Use 'ctp-status' or logout first."); return; }
    var config = await store.GetAsync();
    if (config is null) { Console.WriteLine("No CTP config saved. Run 'ctp' first."); return; }
    try
    {
        var request = new CtpLoginRequest
        {
            FrontAddress = config.FrontAddress,
            MdFrontAddress = config.MdFrontAddress ?? "",
            BrokerId = config.BrokerId,
            UserId = config.UserId,
            Password = config.Password,
            AppId = config.AppId,
            AuthCode = config.AuthCode
        };
        var result = await tradingService.LoginAsync(request);
        Console.WriteLine($"CTP login OK — 交易日={result.TradingDay} FrontID={result.FrontId} SessionID={result.SessionId}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"CTP login failed: {ex.Message}");
    }
}

static async Task<bool> LoginAsync(IAppAuthService authService, ICurrentUser currentUser)
{
    var username = Prompt("Username", "");
    var password = Prompt("Password", "");
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) { Console.WriteLine("Login cancelled."); return false; }
    try
    {
        var result = await authService.LoginAsync(username, password);
        if (result.Success)
        {
            var uid = await currentUser.GetCurrentUserIdAsync();
            Console.WriteLine($"Logged in. Current user: {uid ?? "(unknown)"}");
            return true;
        }
        Console.WriteLine($"Login failed: {result.Error}");
    }
    catch (Exception ex) { Console.WriteLine($"Login error: {ex.Message}"); }
    return false;
}

/// <summary>
/// Like AlphaAgent's PostLoginInitializer.StartBackgroundSyncsAsync: if logged in,
/// fire-and-forget a non-blocking securities sync from the server. Prints when done.
/// </summary>
static Task KickoffBackgroundSyncAsync(ISecurityClientSyncService securitySync, IAppAuthService authService, string label)
{
    _ = Task.Run(async () =>
    {
        try
        {
            if (!await authService.IsLoggedInAsync()) return;
            Console.WriteLine($"[sync:{label}] syncing securities from server...");
            var r = await securitySync.SyncFromServerAsync();
            Console.WriteLine($"[sync:{label}] done — {r.SyncedCount} securities (full={r.IsFullSync}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync:{label}] failed: {ex.Message}");
        }
    });
    return Task.CompletedTask;
}

static async Task ListConfigsAsync(ILlmConfigService llmService)
{
    var list = await llmService.GetCachedAsync();
    if (list is null || list.Count == 0) { Console.WriteLine("(no LLM configs in SQLite — type 'config' to add one)"); return; }
    foreach (var c in list)
        Console.WriteLine($"  {(c.IsDefault ? "*" : " ")} {c.Name} — {c.ModelName} @ {c.Endpoint}{(c.IsSystem ? "  [system]" : "")}");
}

static LlmConfigDto PromptLlmConfigDto()
{
    Console.WriteLine("Enter LLM config (press Enter to accept [default]):");
    return new LlmConfigDto
    {
        Name = Prompt("Name", "console-default"),
        ModelName = Prompt("Model", Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "deepseek-chat"),
        ApiKey = Prompt("ApiKey", Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""),
        Endpoint = Prompt("Endpoint", Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "https://api.deepseek.com"),
        Temperature = float.TryParse(Prompt("Temperature", "0.5"), out var t) ? t : 0.5f,
        IsDefault = true
    };
}

static string Prompt(string label, string def)
{
    Console.Write($"{label}{(string.IsNullOrEmpty(def) ? "" : $" [{def}]")}: ");
    var input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? def : input.Trim();
}

static async Task SaveDefaultConfigAsync(SqliteLlmConfigRepository repo, LlmConfig config)
{
    var existing = await repo.GetAllAsync();
    var changed = existing.Where(o => !o.IsSystem && o.IsDefault).ToList();
    foreach (var o in changed) o.IsDefault = false;
    if (changed.Count > 0) await repo.UpsertRangeAsync(changed);
    await repo.UpsertRangeAsync(new[] { config });
}
