using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Acme.Ctp.AI;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Ctp.Net.Core.Trader;
using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;
using Acme.Llm.Application;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Platform.Domain;
using Acme.Platform.Application;
using Acme.Platform.Application.Auth;
using Acme.Platform.Application.DependencyInjection;
using Acme.Platform.Infrastructure;
using Acme.Platform.Infrastructure.DependencyInjection;
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
using Aether.AI.Hosting;
using Aether.AI.Harness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ---- Aether.AI integrated Console demo -----------------------------------
// Composes every context: Platform (app auth/HTTP) + Llm (LLM config) + Qifu +
// Ctp + Securities + Quotes + Aether.AI. Three agents are wired (qifu / ctp /
// stock-analyst), each with its tools. 'sync-securities' pulls instruments
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
    services.AddPlatformInfrastructure(o =>
    {
        o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL") ?? "https://117.72.186.128/";
        o.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? "alphaagent_chat";
        o.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? "chat_secret";
        o.Tenant = Environment.GetEnvironmentVariable("APP_TENANT") ?? "stockapp";
    });
    services.AddPlatformApplication();
    // Llm: LLM config in SQLite.
    services.AddLlmInfrastructure();
    services.AddLlmApplication();
    // Qifu: 360 internal-platform integration.
    services.AddQifuInfrastructure();
    services.AddQifuApplication();
    // Ctp: futures broker session (self-contained, native CTP binding).
    services.AddCtpInfrastructure();
    // Securities: instruments master data + server sync.
    services.AddSecurityInfrastructure();
    services.AddSecurityApplication();
    // Quotes: market data + indicators + analysis (ACL-bridged to Securities via ai/Quotes).
    services.AddQuotesInfrastructure();
    services.AddQuotesApplication();
}

static void RegisterAgents(AetherAIBuilder aether, string modelId)
{
    // One agent with all tools from every context (no agent switching needed).
    aether.AddQifuTools().AddCtpTools().AddQuotesTools();
    var allTools = QifuAIBuilderExtensions.ToolNames
        .Concat(CtpAIBuilderExtensions.ToolNames)
        .Concat(QuotesAIBuilderExtensions.ToolNames)
        .ToArray();
    aether.AddAgent("assistant", a => a
        .Name("Aether Assistant")
        .Instructions(
            "你是企业助手，可调用工具：证券查询/技术指标、CTP 期货（账户/持仓/行情/下单/撤单）、" +
            "毓数异步 SQL、360Teams 云文档/消息通知。按需调用工具，简洁回复。下单/撤单默认仅预览。")
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
    Console.WriteLine("Aether.AI live. Commands: 'ctp', 'ctp-login', 'ctp-status', 'sync-securities', 'login', 'whoami', 'configs', 'config', 'exit'.");
    Console.WriteLine("One assistant with all tools (qifu/ctp/quotes). Tool backends still need their own login/credentials.");
    Console.WriteLine(new string('-', 60));

    var llmService = services.GetRequiredService<ILlmConfigService>();
    var authService = services.GetRequiredService<IAppAuthService>();
    var currentUser = services.GetRequiredService<ICurrentUser>();
    var securitySync = services.GetRequiredService<ISecurityClientSyncService>();
    var ctpConfigStore = services.GetRequiredService<ICtpConfigStore>();
    var ctpSession = services.GetRequiredService<CtpSessionService>();

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
                ? $"CTP connected (交易日={ctpSession.LoginResult?.TradingDay}, FrontID={ctpSession.LoginResult?.FrontID}, SessionID={ctpSession.LoginResult?.SessionID})"
                : "CTP not connected.");
            continue;
        }

        if (cmd.Equals("sync-securities", StringComparison.OrdinalIgnoreCase))
        {
            await SyncSecuritiesAsync(securitySync);
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

static async Task CtpLoginAsync(ICtpConfigStore store, CtpSessionService session)
{
    if (session.IsConnected) { Console.WriteLine("Already connected. Use 'ctp-status' or logout first."); return; }
    var config = await store.GetAsync();
    if (config is null) { Console.WriteLine("No CTP config saved. Run 'ctp' first."); return; }
    try
    {
        var request = new CtpConnectRequest
        {
            FrontAddress = config.FrontAddress,
            BrokerId = config.BrokerId,
            UserId = config.UserId,
            Password = config.Password,
            AppId = config.AppId,
            AuthCode = config.AuthCode
        };
        var result = await session.LoginAsync(request);
        Console.WriteLine($"CTP login OK — 交易日={result.TradingDay} FrontID={result.FrontID} SessionID={result.SessionID}");
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
