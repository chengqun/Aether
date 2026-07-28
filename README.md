# Aether.AI Platform

An enterprise-grade AI application platform built **on top of** [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

> Microsoft Agent Framework is responsible for the Agent Runtime.
> Aether.AI Platform is responsible for enterprise AI application infrastructure.

Aether.AI does **not** re-implement the agent framework — there is no `IAIAgent`, `IAgentRuntime`, `ITool`, or `IWorkflow` here. Those exist in Microsoft Agent Framework already. Aether.AI is the platform layer above it.

> **Current architecture authority: [`CLAUDE.md`](CLAUDE.md).** The platform has grown well beyond the
> Phase-1 snapshot described below (it now spans 17 DDD bounded contexts, an Avalonia desktop app with
> two tenants, custom-agent/marketplace, Automation, CTP 期货, Quotes, and Trading+Lean). `CLAUDE.md` is the
> maintained, authoritative architecture reference; the structure tree and context narratives further down
> are an early snapshot and may lag — cross-check `CLAUDE.md` and the code for the current state.

## Status

Phase 1 implements **Aether.AI.Harness** — a runtime that can be invoked from WPF, MAUI, Web, and background services.

Implemented:

- Agent creation & configuration (`AgentBuilder` / `AgentDefinition` / `AgentFactory`)
- Harness Runtime (`HarnessRuntime`) wrapping the framework's `HarnessAgent`
- Tool registration (`ToolRegistry`)
- Session management (`SessionManager`) — Aether runtime context
- Memory management (`IMemoryStore`, `IConversationStore`, `MemoryService`) with an InMemory backend
- DI integration (`services.AddAetherAI()`)
- Console demo validating multi-turn conversation, tool calls, and memory save/query

Not in Phase 1 (planned for later phases): Workflow, RAG, MCP, Multi-Agent, Vector Database, business Tools.

## Solution structure

There is a single solution at the root, `Aether.AI.Platform.slnx`. Platform
source lives under `src/` and consumers (the console sample, future WPF/MAUI/Web)
live under `apps/` as peers. Consumers reference the platform via project
references, so the platform source stays self-contained and independent even
though everything builds from one solution.

```
Aether/                                # root — single solution, everything here
├── Aether.AI.Platform.slnx            # the one solution (platform + business + ai + tests + apps)
├── src/                               # platform source
│   ├── Aether.AI.Core                 # framework-agnostic domain models (no Agent Framework / OpenAI / DB deps)
│   ├── Aether.AI.Harness              # Microsoft Agent Framework wrapper (AgentFactory, HarnessRuntime, SessionManager, ToolRegistry)
│   ├── Aether.AI.Memory               # IMemoryStore + IConversationStore, InMemory backends, MemoryService
│   └── Aether.AI.Hosting              # AddAetherAI() DI entry point + configuration
├── business/                          # pure DDD business systems (NO AI / NO Aether dependency)
│   └── Qifu/                          # bounded context: 360 internal-platform integration
│       ├── Acme.Qifu.Domain           # qifu contracts + models + IQifuConfigCache port (zero deps)
│       ├── Acme.Qifu.Application      # token-sync services (→ Domain, Platform.Domain)
│       └── Acme.Qifu.Infrastructure   # CloudDocs/Yushu service impls + SqliteQifuConfigCache (→ Domain, Platform.Domain)
├── business/Platform/                 # foundation context: app-server auth + authenticated HTTP + current user
│   ├── Acme.AppAuth.Domain           # ICurrentUser, IAppServerHttpClient, IAppTokenStore, AppServerOptions
│   ├── Acme.AppAuth.Application      # IAppAuthService / AppAuthService (OAuth2 login/refresh/logout)
│   └── Acme.AppAuth.Infrastructure   # SqliteAppTokenStore, BearerTokenDelegatingHandler, AppServerHttpClient, CurrentUserResolver
├── business/Llm/                      # bounded context: LLM provider configs (persist + sync from server)
│   ├── Acme.Llm.Domain                # LlmConfig + ILlmConfigRepository port + LlmConfigDto (zero deps)
│   ├── Acme.Llm.Application           # LlmConfigService (GetCached/SyncFromServer/SetConfig) (→ Domain, Platform.Domain)
│   └── Acme.Llm.Infrastructure        # SqliteLlmConfigRepository (llm_config table) (→ Domain)
├── business/Ctp/                      # bounded context: CTP futures trading (broker session + trading)
│   ├── Acme.Ctp.Domain                # CTP contracts/models + ICtpSession (zero deps, SDK-free)
│   ├── Acme.Ctp.Application           # ICtpTradingService / CtpTradingService (→ Domain)
│   └── Acme.Ctp.Infrastructure        # CtpSessionService + CtpSessionMapper (→ Domain, Ctp.Net.Core)
├── business/Security/                 # bounded context: securities master data (instruments + sync)
│   ├── Acme.Security.Domain           # Security entity + ISecurityRepository/ISecurityManager/ISyncMetadataStore (ns Acme.Securities.Domain)
│   ├── Acme.Security.Application      # SecurityManager, SecurityService, SecurityClientSyncService (→ Domain, Platform.Domain)
│   └── Acme.Security.Infrastructure   # SqliteSecurityRepository + SqliteSyncMetadataStore (→ Domain)
├── business/Quotes/                   # bounded context: market quotes + indicators + analysis (→ Security one-way)
│   ├── Acme.Quotes.Domain             # Quote entity + IQuoteProvider/IQuoteRepository/IIndicatorCalculator/IAnalysisManager (zero deps)
│   ├── Acme.Quotes.Application        # AnalysisManager (→ Quotes.Domain, Security.Domain)
│   └── Acme.Quotes.Infrastructure     # Sina/East/Baidu providers + Failover + IndicatorCalculator (Skender) + SqliteQuoteRepository
├── externals/Ctp.Net.Core/            # managed wrapper over native CTP libs (P/Invoke libctp_native; brought from AlphaAgent)
├── ai/                                # business AI adapters (bridge Aether ↔ business; NOT in business/)
│   ├── Qifu/Acme.Qifu.AI              # 3 tools (CloudDocsUpload/TeamsMessage/YushuSqlQuery) + AddQifu()
│   ├── Ctp/Acme.Ctp.AI                # 6 tools (login/account/position/marketdata/order) + AddCtp()
│   └── Quotes/Acme.Quotes.AI          # 2 tools (QuerySecurity/CalculateIndicators) + AddQuotes() (→ Security.App, Quotes.App)
├── tests/                             # all test projects
│   ├── Aether.AI.Harness.Tests / Aether.AI.Memory.Tests
│   ├── Acme.Qifu.Infrastructure.Tests (SQLite config cache)
│   ├── Acme.AppAuth.Infrastructure.Tests (SQLite session store)
│   ├── Acme.Llm.Infrastructure.Tests (SQLite llm_config)
│   ├── Acme.Ctp.AI.Tests (CTP agent + 7 tools wiring)
│   └── Acme.Quotes.AI.Tests (stock-analyst agent + 2 tools wiring)
└── apps/                              # consumers (peers of src/, not platform code)
    └── Aether.AI.Sample.Console       # references ../../src/Aether.AI.* via project refs
```

Dependency graph (matches the design document):

```
apps/ (Console, future WPF/MAUI/Web)
        ↓
Aether.AI.Hosting → Aether.AI.Harness → Aether.AI.Core
                  ↘ Aether.AI.Memory ↗

ai/Qifu (tools) → Aether.AI.Hosting
                ↘ business/Qifu.Application → business/Qifu.Domain
                                           ↘ business/Platform.Domain   (foundation)
business/Qifu.Infrastructure → business/Qifu.Domain, business/Platform.Domain   (no AI, no Aether)
business/Llm.*               → business/Llm.Domain, business/Platform.Domain   (no AI, no Aether)
business/Ctp.*               → business/Ctp.Domain, externals/Ctp.Net.Core      (no AI, no Aether; self-contained — no Platform dep)
ai/Ctp (tools)               → Aether.AI.Hosting, business/Ctp.Infrastructure, externals/Ctp.Net.Core
business/Security.*          → business/Security.Domain (+ Platform.Domain for sync)   (no AI, no Aether)
business/Quotes.*            → business/Quotes.Domain, business/Security.Domain       (one-way Quotes→Security; no cycle)
ai/Quotes (tools)            → Aether.AI.Hosting, business/Security.Application, business/Quotes.Application
business/Platform.*          → business/Platform.Domain   (foundation; no qifu/llm/ctp/security, no AI, no Aether)
```

### Business contexts

**Platform** (`business/Platform/`) is the *foundation* context: app-server
(ABP) authentication + the authenticated HTTP client + current user, consumed by
other contexts. It owns the cross-cutting ports so they scale to many contexts:

- **Domain** — `ICurrentUser`, `IAppServerHttpClient`, `IAppTokenStore`,
  `IAppTokenRefresher`, `AppServerOptions`.
- **Application** — `IAppAuthService` / `AppAuthService` (OAuth2 password grant
  login/refresh/logout via `connect/token`; persists tokens to `IAppTokenStore`).
- **Infrastructure** — `SqliteAppTokenStore` (session in `platform__session`),
  `BearerTokenDelegatingHandler` (attaches bearer, refreshes on 401, retries),
  `AppServerHttpClient` (authenticated HTTP), `CurrentUserResolver` (JWT `sub`).

**qifu** (`business/Qifu/`) integrates the 360 internal platforms (CloudDocs +
Yushu) + Teams. DDD layers stay free of AI/Aether; they consume Platform's
`ICurrentUser` + `IAppServerHttpClient`:

- **Domain** — qifu service contracts + models + `IQifuConfigCache` (qifu's own
  token/config cache port; replaces AlphaAgent's EF SQLite config repos).
- **Application** — token-sync services (push/pull qifu tokens to the app server
  via Platform's `IAppServerHttpClient`).
- **Infrastructure** — `CloudDocsAuthService`/`CloudDocsService`/`YushuAuthService`/
  `YushuService` (ported ~2400 lines) + `SqliteQifuConfigCache` (`qifu_config_cache`).

**Llm** (`business/Llm/`) manages LLM provider configs (DeepSeek / GPT-4o / local
…): persist to local SQLite, pull from the app server. Consumes Platform's
`IAppServerHttpClient` (authenticated sync). Ported from AlphaAgent's
`LlmConfigService` / `ILlmConfigCacheRepository`. **No user column** — the local
device is single-user (identity comes from app login; server sync is bearer-auth'd,
so the server knows the user without a local column):

- **Domain** — `LlmConfig` entity (no `UserId`; `IsSystem` flags server-managed
  configs), `ILlmConfigRepository` port (`GetAllAsync`/`UpsertRangeAsync`/`ClearAsync`),
  `LlmConfigDto`.
- **Application** — `LlmConfigService`: `GetCachedAsync`, `SyncFromServerAsync`
  (GET `api/app/llm-config/my-llm-configs`, full-replace local cache), `SetConfigAsync`
  (local save, default-mutual-exclusive; does not push to server).
- **Infrastructure** — `SqliteLlmConfigRepository` (`llm_config` table, WAL+busy_timeout).

> **Scope note.** Plan A only: config persistence + pull-from-server. Not included
> (future plan B): an `ILLMConfigProvider` port in `Aether.AI.Core` so the Aether.AI
> platform builds `IChatClient` from the persisted active config (today the platform
> still uses static `AetherAIOptions`). Sync is pull-only, matching AlphaAgent; user
> configs are local-only (no push).

**Ctp** (`business/Ctp/` + `externals/Ctp.Net.Core/`) — CTP futures trading: broker
session, account/position/market-data queries, order insert/cancel. Ported from
AlphaAgent's `AlphaAgent.{Application,Infrastructure}.Ctp` + `src/Ctp.Net.Core`.
Self-contained — no Platform/qifu dependency, no SQLite (the session is an
in-memory connection to the CTP broker, not a persisted store):

- **Domain** — CTP enums/exceptions/models + `ICtpSession` (SDK-free contract; DTO surface).
- **Application** — `ICtpTradingService` / `CtpTradingService` (thin pass-through to `ICtpSession`).
- **Infrastructure** — `CtpSessionService : ICtpSession` (wraps `CtpTrader`/`CtpMarketData`
  lifecycle) + `CtpSessionMapper` (SDK ↔ DTO). Depends on `externals/Ctp.Net.Core`.
- **externals/Ctp.Net.Core** — managed wrapper over the native CTP libs
  (P/Invoke `libctp_native.dylib/.so/.dll`; prebuilt native copied as Content, no C++ toolchain needed to build).
- **ai/Ctp** — 6 tools (login/status, account, position, market-data, order insert,
  order cancel) + `AddCtp()`. Tools inject `CtpSessionService` and return native SDK
  types to the LLM. 7 AIFunctions registered (note: MEAI strips the `Async` suffix
  from method names — tool names are `GetStatus`/`QueryAccount`/`QueryPositions`/
  `OrderInsert`/`OrderAction`/`QueryLatestTick`/`QueryAllLatestTicks`).

> **Scope note.** The market-data "main futures" loader now lives in the
> `Securities` context (`ISecurityManager.GetMainFuturesAsync()`), and the Lean-backed
> strategy engine is now its own context — `business/Trading/` + `business/Trading.Runtime/`
> (see [Trading + Lean](#trading--lean-backtest--live-trading) below). The self-contained
> Ctp core (session + trading + tools) plus the full Trading/Lean stack are ported.

The 3 AI tools live in `ai/Qifu/Acme.Qifu.AI` (NOT in `business/`), and call the
qifu Domain service contracts — never the Infrastructure implementations directly.

> **Implementation status.** The qifu bounded context is a **full faithful port**
> from AlphaAgent (`src/AlphaAgent.Core/AlphaAgent.{Application,Infrastructure}.qifu`):
> all six service implementations (CloudDocs/Yushu auth + service, plus the two
> token-sync services, ~2400 lines) are ported verbatim. AlphaAgent's shared-kernel
> dependencies were redistributed to the right contexts:
> `IYushuConfigCacheRepository`/`ICloudDocsConfigCacheRepository` (EF SQLite) →
> qifu's `IQifuConfigCache` + `SqliteQifuConfigCache` (`qifu_config_cache`, path
> from `sqliteDbPath` / `QIFU_DB_PATH` / `~/.qifu/qifu.db`); `IUserIdResolver`
> and `IHttpClientService` → Platform's `ICurrentUser` + `IAppServerHttpClient`
> (so app-server auth is a foundation, not qifu's concern). CloudDocs login uses
> `Microsoft.Playwright` (run `playwright install chromium`). Runtime still needs
> real 360 credentials + ABP server config; not exercisable in this environment.
>
> **Sync-server auth (resolved).** The token-sync calls
> (`api/app/...-token/save-my-token`) hit the company ABP server and need the app
> user's bearer token. AlphaAgent attached it via ABP's
> `BearerTokenDelegatingHandler`; Aether's Platform context now provides the
> equivalent — `AppServerHttpClient` rides a `BearerTokenDelegatingHandler` that
> attaches the cached app token and auto-refreshes on 401. So qifu's
> `IAppServerHttpClient` (from Platform) is already authenticated; qifu never
> handles app-server auth itself.

Host wiring:

```csharp
// Foundation: app-server auth + authenticated HTTP + current user
services.AddPlatformInfrastructure(o => { o.BaseUrl = "https://app.example.com/"; o.ClientId = "..."; o.ClientSecret = "..."; });
services.AddPlatformApplication();          // IAppAuthService (login/refresh/logout)

// Llm: LLM provider configs in SQLite (pull from server)
services.AddLlmInfrastructure();
services.AddLlmApplication();

// qifu: 360 platform integrations (consumes Platform's ICurrentUser + IAppServerHttpClient)
services.AddQifuInfrastructure();
services.AddQifuApplication();

// AI: qifu assistant agent + 12 tools
services.AddAetherAI().AddQifu(modelId);

// To log in (acquires + caches the app bearer token, enabling authenticated sync):
await app.Services.GetRequiredService<IAppAuthService>().LoginAsync(user, pass);
```

> **Auth HTTP cycle note.** `AppAuthService` (login/refresh) uses a *plain*
> `HttpClient` (base address only, no bearer handler) for `connect/token`. The
> authenticated `IAppServerHttpClient` (with `BearerTokenDelegatingHandler`) is
> for business calls. Routing login through the authenticated client would cycle
> (handler → `IAppTokenRefresher` → `IAppServerHttpClient` → handler).

The contexts persist to SQLite (`~/.aether/llm.db`, `~/.aether/platform.db`,
`~/.qifu/qifu.db`; pass `sqliteDbPath` / set `LLM_DB_PATH` / `PLATFORM_DB_PATH` /
`QIFU_DB_PATH` to colocate). Each owns its tables (`llm_config`,
`platform__session`, `qifu_config_cache`); WAL + busy_timeout are set so
concurrent writers cooperate. The Aether.AI IChatClient factory honors
`AetherAIOptions.OpenAI.Endpoint` (OpenAI-compatible providers like DeepSeek).

### Console demo (`apps/Aether.AI.Sample.Console`)

An integrated host composing **all** contexts — no platform LLM-config port
("plan B") needed: the Console reads the LLM config from SQLite and assembles it
into the agent itself:

1. **LLM config in SQLite** — on startup, reads the active config from
   `llm_config`; if none and `OPENAI_API_KEY` is set, seeds one (model/endpoint
   from `OPENAI_MODEL` / `OPENAI_ENDPOINT`, default DeepSeek); otherwise prompts
   interactively. `--config` forces re-entry.
2. **Read from SQLite → assemble into agent** — the active config's
   ApiKey/ModelName/Endpoint are written into `AetherAIOptions`, so `AddAetherAI`
   builds the `IChatClient` from the persisted config.
3. **Login to the app server** — `login` calls `IAppAuthService.LoginAsync`
   (Platform OAuth2 password grant), caching the bearer token in
   `platform__session`; `whoami` shows the resolved current user (from the JWT).
   Needs `APP_BASEURL` / `APP_CLIENT_ID` / `APP_CLIENT_SECRET` env vars.
4. **Actually call qifu tools** — `AddQifu(modelId)` wires the qifu agent + 12
   tools; the conversation loop drives the agent, which can invoke qifu tools.
   Chat works without login; qifu tools need `login` (real `ICurrentUser`),
   and their backends still need real 360 login + credentials.

Conversation-loop commands: `login`, `whoami`, `configs` (list), `config` (enter
new, saved for next run), `exit`.

```bash
export OPENAI_API_KEY=sk-...             # seeded into SQLite as the default LlmConfig
export OPENAI_MODEL=deepseek-chat        # optional
export OPENAI_ENDPOINT=https://api.deepseek.com   # optional
# For 'login' (app server):
export APP_BASEURL=https://app.example.com/
export APP_CLIENT_ID=...
export APP_CLIENT_SECRET=...
dotnet run --project apps/Aether.AI.Sample.Console
```

LLM config can also be entered interactively on the command line — if SQLite has
no config and no `OPENAI_API_KEY` is set, the Console prompts for
name/model/apiKey/endpoint/temperature and saves it as the default. `--config`
forces re-entry (adds/replaces the default). Inside the conversation loop,
`configs` lists the stored configs and `config` enters a new one (saved for the
next run; the running agent keeps its current LLM).

Without an API key it runs an offline smoke check (builds the composed host,
verifies the qifu agent + tools register). Live qifu tool execution still needs
real 360 login + credentials (Playwright/OAuth) — the wiring is real, the
backends need creds.

## Trading + Lean (backtest + live trading)

The desktop **回测/实盘** page (Stockapp `backtest` nav), the business **Trading**
context, and the **Lean** runtime are four separated layers with a one-way
dependency. The UI and business layers never reference Lean — swapping the
runtime is a DI-only change.

```
Desktop 回测/实盘页   (TradingViewModel + TradingView, apps/Stockapp)        ← runtime-agnostic
        ↓ depends on
Trading.Application   (IBacktestApplicationService / ILiveTradingApplicationService)   ← orchestration, Lean-neutral
        ↓ depends on
Trading.Runtime.Domain   (ports: IBacktestRuntime / ILiveTradingRuntime; BarResolution; LiveTradingRuntimeStatus; MarketDataConnectInput)
        ↑ implemented by
Trading.Runtime.Lean   (LeanBacktestRunner / LeanLiveTradingService / CtpBrokerage / TradingHostAlgorithm)   ← the ONLY project referencing externals/Lean
        ↓ consumes (DI-provided, not self-registered): ICtpSession (Ctp context) + IAnalysisManager (Quotes context)

Trading.Domain   (IStrategy / MarketBar / SignalDirection / BacktestResult / BacktestStatistics / EquityPoint / StrategyRegistry)   ← pure, shared by Application + Lean
```

- **`business/Trading/Acme.Trading.Domain`** — pure: `IStrategy`, `MarketBar`, `BacktestResult`
  (`Statistics` / `EquityCurve` / `Logs`), `StrategyRegistry` (static; `AddTradingRuntimeLean`
  registers `SmaCross`/`Test`; `GetRegisteredNames()` feeds the UI strategy picker).
- **`business/Trading/Acme.Trading.Application`** — `IBacktestApplicationService.RunAsync(BacktestRunInput)→BacktestResult`
  and `ILiveTradingApplicationService` (`StartAsync`/`StopAsync`, `Status`/`IsRunning`,
  `StatusChanged`/`LogReceived` events). Thin mappers to the runtime ports. No Lean.
- **`business/Trading.Runtime/Acme.Trading.Runtime.Domain`** — the runtime **ports**
  (`IBacktestRuntime`, `ILiveTradingRuntime`) + shared enums/DTOs. No Lean.
- **`business/Trading.Runtime/Acme.Trading.Runtime.Lean`** — the **only** project referencing
  `externals/Lean`. `LeanBacktestRunner` reads K-line via `IAnalysisManager` → CSV → runs the LEAN
  backtest engine → `BacktestResult`. `LeanLiveTradingService` runs the LEAN live engine with
  `CtpBrokerage`. `AddTradingRuntimeLean()` registers the strategies + both runtime impls; it
  **consumes** (does not register) `ICtpSession` and `IAnalysisManager`.
- **Desktop page** (`apps/Stockapp/.../ViewModels/TradingViewModel.cs` + `Views/TradingView.axaml`):
  two tabs — 回测 (parameter form + statistics + equity `Polyline` + log) and 实盘 (start/stop +
  status + signal log). The VM depends on `Trading.Application` + `Trading.Runtime.Domain` enums +
  `Trading.Domain` only — **never** on `Trading.Runtime.Lean`. Wired via the tenant-specific
  ContentControl + DataTemplate pattern (Stockapp-only; Qifu has no `backtest` nav).
- **AI** (`ai/Trading/Acme.Trading.AI`): `AddTradingTools()` exposes only `RunBacktest` (no
  live-trading tool to the agent).

Runtime paths:

- **Backtest**: `RunCommand` → `Task.Run(() => _backtest.RunAsync(input))` (blocking, so off the UI
  thread) → `BacktestResult`. **Data caveat**: K-line comes from `IAnalysisManager` (Quotes context,
  stock-oriented Sina/East/Baidu providers) — futures symbols may have no data; stocks work.
- **Live**: `StartLiveCommand` → `_live.StartAsync(LiveTradingRunInput)`; preconditions: `ICtpSession`
  already logged in (CTP connected via the 期货 page) + a complete `MarketDataConnectInput` (md front,
  built from `CtpConnectorViewModel`). **P1 = signal-only**: `CtpBrokerage.PlaceOrder` logs the
  intended order and returns `false` — no real CTP order is placed; the tick→bar→signal chain is live.
  P2 (real orders) is future work.
- **Swapping the runtime** (Lean → other): implement `IBacktestRuntime`/`ILiveTradingRuntime` in a new
  project and replace the `AddTradingRuntimeLean()` registration. `Trading.Application`,
  `Trading.Domain`, and the desktop VM are untouched.

Host wiring (Stockapp `App.axaml.cs`):

```csharp
services.AddCtpInfrastructure(...);   // ICtpSession  (live engine consumes)
services.AddQuotesInfrastructure(...); services.AddQuotesApplication();   // IAnalysisManager (backtest data)
services.AddTradingApplication();     // IBacktestApplicationService + ILiveTradingApplicationService
services.AddTradingRuntimeLean();     // IBacktestRuntime + ILiveTradingRuntime (Lean impl) + strategy registration
```

## Key design decisions

- **Core is dependency-free.** `Aether.AI.Core` has no reference to Microsoft Agent Framework, the OpenAI SDK, or any database. It only defines plain models (`Message`, `AgentSession`, `Conversation`, `MemoryItem`, `AgentDefinition`).
- **Two `AgentSession` types, deliberately.** Microsoft Agent Framework names its conversation-thread type `AgentSession`; Aether.AI.Core also names its runtime context `AgentSession`. The Harness layer aliases them (`FrameworkAgentSession` vs `AetherSession`) — the framework's tracks model-side chat history, Aether's tracks runtime execution state (messages, tool calls, lifecycle).
- **Memory ≠ Session.** A `Session` is short-lived runtime context. `MemoryItem` / `Conversation` are durable knowledge that survives across sessions.
- **Lazy chat client.** The OpenAI `IChatClient` is resolved lazily via `Func<IChatClient>` so that composing the DI container (and running the offline smoke check) does not require an API key.
- **HarnessAgent with providers disabled.** `AgentFactory` builds a `HarnessAgent` by default but disables the heavyweight built-in providers (web search, todos, file memory, skills, OpenTelemetry) so Phase 1 is a clean, dependency-light runtime. Set `AetherAIOptions.UseChatClientAgent = true` to use the lighter `ChatClientAgent` instead.

## Build & test

Requires the .NET 10 SDK.

```bash
dotnet build Aether.AI.Platform.slnx
dotnet test  Aether.AI.Platform.slnx
```

## Run the console demo

See the **Console demo** section above — `apps/Aether.AI.Sample.Console` is now an
integrated host (Platform + Llm + Qifu + Aether.AI) that reads the LLM config
from SQLite and drives the qifu agent. `dotnet run --project apps/Aether.AI.Sample.Console`
(with `OPENAI_API_KEY` to seed the SQLite config; otherwise an offline smoke check).

## Wiring it into your own host

```csharp
var builder = Host.CreateApplicationBuilder(args);

var aether = builder.Services.AddAetherAI();
aether.Configure(opts => opts.OpenAI.ModelId = "gpt-4o-mini");

aether.AddAgent("sales-assistant", a => a
    .Name("Sales Assistant")
    .Instructions("You help the sales team.")
    .WithTools(["GetWeather"]));

aether.AddTool((Func<string, string>)MyBusinessTools.GetWeather);

var app = builder.Build();
var runtime = app.Services.GetRequiredService<HarnessRuntime>();
var memory = app.Services.GetRequiredService<MemoryService>();

var session = runtime.StartSession("sales-assistant");
var reply = await runtime.RunAsync(session.SessionId, "What's the weather in Shanghai?");
```

## Business integration

Per the design document, business code follows DDD and talks to Aether through an AI adapter — the Domain layer never depends on AI:

```
YourCompany.AI/Sales/Agents & Tools  →  Business.Application  →  Business.Domain
```
