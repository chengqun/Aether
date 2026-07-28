# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build, test, run

Single solution: `Aether.AI.Platform.slnx` (note: `.slnx`, not `.sln`). .NET 10.

```bash
# Lean is a git submodule — init before first build (shallow, pinned to a fixed commit)
git submodule update --init externals/Lean

dotnet build Aether.AI.Platform.slnx          # build everything
dotnet test  Aether.AI.Platform.slnx          # run all test projects

# Single project build/test (dotnet build/test accept ONLY ONE project in this env)
dotnet test tests/Aether.AI.Harness.Tests/Aether.AI.Harness.Tests.csproj --filter "FullyQualifiedName~Memory"

# Run the sample console (composes all contexts). Without an LLM API key it runs an offline smoke check.
dotnet run --project apps/Aether.AI.Sample.Console

# Run the desktop app (Avalonia; Stockapp tenant with CTP/Quotes/Trading; Qifu tenant with Yushu/CloudDocs)
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop
dotnet run --project apps/Qifu/Acme.Qifu.Desktop
```

The first build after init is slow (Lean source compiles). Lean's transitive deps emit NU1902/3/4 vulnerability advisories (System.Drawing.Common / WCF / WinHttpHandler) — advisory only, expected.

## Big-picture architecture

A multi-context DDD platform built on **Microsoft Agent Framework** (`Microsoft.Agents.AI`, wrapped by `src/Aether.AI.Harness`). Top-level layout enforces a strict layering:

- `src/` — the **Aether.AI platform** itself, standalone. `Aether.AI.Core` (framework-agnostic domain, zero deps), `Aether.AI.Harness` (AgentFactory/HarnessRuntime/SessionManager/ToolRegistry/IChatClientFactory wrapping MEAI), `Aether.AI.Memory` (IConversationStore abstraction + InMemory impl), `Aether.AI.Memory.Sqlite` (SQLite impl, opt-in), `Aether.AI.Hosting` (`services.AddAetherAI()` DI entry point + DefaultChatClientFactory + ConversationTitleService). Platform source never references `business/` or `ai/`.
- `business/` — **pure DDD bounded contexts**, each `Acme.<Context>.{Domain,Application,Infrastructure}`. AI-free.
- `ai/` — **business AI adapters** bridging Aether and each context. Peers of `business/`, kept separate so business stays AI-free. 7 tool adapters (`Ctp/Quotes/Qifu/Notes/Trading/ApkParser/SystemTools`) + 1 bridge adapter (`Agents`). Not every context has an AI adapter — e.g. Automation has none (its business port `IAgentExecutor` is implemented host-side; see [business-port pattern](#business-domain-port--host-implementation) below).
- `externals/` — third-party engines: `Ctp.Net.Core` (native CTP wrapper) and `Lean` (QuantConnect Lean, git submodule).
- `apps/` — consumers/hosts: `Aether.AI.Sample.Console` (CLI), `Acme.Shell` (UI-agnostic shell VM library), `Desktop/Acme.Desktop.Shared` (Avalonia views + composition root), `Qifu/Acme.Qifu.Desktop` + `Stockapp/Acme.Stockapp.Desktop` (tenant host apps).
- `tests/` — all test projects live here, never interleaved with source.

### DDD layering rules (enforced; verify before changing csprojs)

- **Domain** = pure: zero `ProjectReference`, no SDK/HTTP/SQLite/logging packages, no `Aether.AI.*`. Entities/value-objects/ports only. A Domain may reference another context's Domain (shared kernel) — e.g. 8 contexts reference `Acme.AppAuth.Domain`.
- **Application** → Domain (+ foreign Domain port). Never Infrastructure/AI/externals.
- **Infrastructure** → Domain (+ externals + foreign Domain contract). Never another context's Infrastructure.
- **`business/` must NEVER reference `Aether.AI.*`.**
- Dependency graph is a DAG; no cycles. `Acme.AppAuth.Domain` is the pure source (foundation), `Acme.Trading.Runtime.Lean` the pure sink.

### AI adapter convention (`ai/<Context>/Acme.<Context>.AI`)

Every **tool adapter** is the same shape (mirror `ai/Ctp`):
- csproj: `Aether.AI.Hosting` + the context's **Application** project (never Infrastructure/Domain directly).
- `AddXxxTools(this AetherAIBuilder)` registers tools ONLY — there is **no** `AddXxx(modelId)` agent method. Agents are built dynamically by the host/page from the shared tool pool. `ToolNames[]` + `CreateTool<T>` helper using `AIFunctionFactory.Create`.
- Tool classes live in `Tools/`, namespace `Acme.<Context>.AI.Tools`. Tools call Application contracts, never Domain/Infrastructure.
- `AddXxxTools` registers TOOLS ONLY — the host composition root wires business layers.

The **bridge adapter** `ai/Agents/Acme.Agents.AI` is a different shape:
- References `Aether.AI.Hosting` + `Acme.Notes.Application` (custom agents persisted as Notes) + `Acme.AppAuth.Application` (for `IAppServerHttpClient`, marketplace client).
- `AddCustomAgents(this AetherAIBuilder)` registers `ICustomAgentCatalog` (CRUD over Notes-backed agents via `AgentSourceParser`), `ICustomAgentRunner` (maps `CustomAgentDefinition` → `AgentDefinition` → `HarnessRuntime`), `IAgentMarketplaceService` (cloud agent catalog client).
- Custom agent persistence: `AgentSourceParser` (YAML front-matter + Markdown prompt) in `business/Notes/Acme.Notes.Application/AgentSource/`. Fields: name/description/category/tools/memory/max_history/model/source/marketplace_id/marketplace_synced_at/display_name.

### Business Domain port → host implementation

When **business logic must trigger agent execution** (or anything AI/platform-specific) but business cannot reference `Aether.AI.*`, define a **port interface in the context's Domain** and implement it **in the host composition root**. This is the canonical pattern — do NOT create an `ai/` adapter just to host a port impl, and do NOT make business depend on AI.

- **Port** in `Acme.<Context>.Domain.Interfaces` (e.g. `IAgentExecutor`). Domain stays pure; the Infrastructure service/scheduler depends on the port, not on AI types.
- **Implementation** lives in an AI-aware host project (`apps/Acme.Shell` or `apps/Desktop/...`), depending on platform/AI types (`ICustomAgentRunner`, `IConversationStore`, …), NEVER on business Infrastructure.
- **Registration** at the composition root (`AddDesktopCommon`): `services.AddSingleton<IPort, HostImpl>()`.
- **Reuse, don't re-route**: the impl should call the *same* runtime path the UI uses, not a parallel one. Canonical example — `ConversationBackedAgentExecutor` (`apps/Acme.Shell`): Automation's scheduler calls `IAgentExecutor.ExecuteAsync`; the impl runs the agent via `ICustomAgentRunner.RunStreamingAsync` and persists the turn to `IConversationStore`, so scheduled runs land in「最近会话」with cross-fire memory — identical to the chat page's flow. Each task owns a stable `ConversationId` (stored on the entity) so fires thread into one conversation.
- The business layer raises completion via a port event (`IAutomationScheduler.TaskCompleted`); the host VM subscribes and refreshes UI (task list + recent conversations) on the dispatcher thread.

Reference: Automation is the working template — `business/Automation/` (Domain port + scheduler) + `apps/Acme.Shell/ConversationBackedAgentExecutor.cs` (host impl) + `AddDesktopCommon` (registration).

### Foundation context

`business/AppAuth/` (`Acme.AppAuth.*`, DI classes named `Platform*Extensions`) is the **foundation**: app-server (ABP) OAuth2 auth + authenticated HTTP (`IAppServerHttpClient` with `TenantHeaderDelegatingHandler` + `BearerTokenDelegatingHandler`) + `ICurrentUser`. Other contexts consume its ports — never the reverse.

### Trading + Lean (the engine-bridge pattern)

- `business/Trading/` — pure DDD: Domain (`IStrategy`/`MarketBar`/`BacktestResult`) + Application (orchestration, depends on Runtime abstraction only).
- `business/Trading.Runtime/` — the bridge: `Runtime.Domain` (abstraction ports) + `Runtime.Lean` (the ONLY project referencing `externals/Lean`).

### Desktop architecture

```
apps/Acme.Shell/                    ← UI-agnostic shell VM library (zero Avalonia, refs CommunityToolkit.Mvvm)
  MainViewModelBase (abstract chat shell: conversations/messages/agents/sidebar)
  ExpertsViewModel (专家/连接器/市场 3-tab)
  ExpertsViewModel/ExpertEditViewModel/ExpertItem (custom agent CRUD)
  NotesViewModel, AutomationViewModel (+AutomationModels), LoginViewModel, LlmConfigEditViewModel
  ConversationBackedAgentExecutor (IAgentExecutor host impl — business-port pattern)
  IUiDispatcher (InvokeAsync+Post), IConnectorViewModel, IDeviceAutoConnectService
  ILoginDialogService, ISettingsDialogService, IPreferencesStore (interfaces)
  IChatViewModel, ConversationItem, ChatMessageItem, NavItem
apps/Desktop/Acme.Desktop.Shared/   ← Avalonia views (axaml) + composition root
  Hosting/DesktopCompositionExtensions (AddDesktopCommon: shared DI for both tenants)
  Hosting/JsonPreferencesStore, LlmConfigModelSource, DesktopChatClientFactory
  Shell/AvaloniaDispatcher, LoginDialogService, SettingsDialogService, DeviceAutoConnectService
  Shell/Controls/SelectableMarkdownViewer (Markdown.Avalonia.Tight)
  Themes/SharedTheme.axaml
apps/Stockapp/  ← tenant host (CTP/Quotes/Trading + CTP connector)
apps/Qifu/      ← tenant host (Yushu/CloudDocs + Qifu connector)
```

### Per-agent model + memory (platform layer)

- **Per-agent ModelId**: `AgentDefinition.ModelId` (opaque string) resolved by `IChatClientFactory.Create(modelId)` → `IChatClient`. Platform default (`DefaultChatClientFactory`): uses `AetherAIOptions.OpenAI` key/endpoint. Desktop override (`DesktopChatClientFactory`): maps ModelId = LlmConfig.Id → builds per-provider client.
- **Memory policy**: `AgentDefinition.Memory` (`Full`/`SlidingWindow`/`Off`) + `MaxHistory`. Enforced at agent build time via `HarnessAgentOptions.ChatHistoryProvider`: Full→null (default InMemory), SlidingWindow→`InMemoryChatHistoryProvider` + `SlidingWindowChatReducer`, Off→`NoMemoryChatHistoryProvider`.
- `HarnessRuntime.Invalidate(agentId)` drops cached agent + definition (on edit).

### Conversation persistence (ChatGPT-style)

- `IConversationStore` (abstraction in `Aether.AI.Memory`) + `SqliteConversationStore` (impl in `Aether.AI.Memory.Sqlite`, opt-in). Desktop uses `conversations.db` in tenant dir.
- `Conversation` has `AgentId`/`AgentName`/`Title`/`Messages`. `ConversationSummary` for sidebar (no full transcript).
- New chat = lazy create (first send). `ICustomAgentRunner` keys runtime sessions by `conversationId` (per-conversation memory isolation). Restart restores display only (agent context fresh).
- `IConversationTitleService` (Hosting) — LLM auto-generates title from first turn (fire-and-forget, 4-12 chars).
- `IPreferencesStore` (desktop `JsonPreferencesStore`) — persists selected agent, summoned list, LLM selection, sidebar expand state.

### Host composition

- **Sample console** (`apps/Aether.AI.Sample.Console/Program.cs`): `ComposeContexts` registers all contexts' Infra+App; `RegisterAgents` chains `AddXxxTools()` + builds one aggregate `assistant` agent.
- **Desktop** (`AddDesktopCommon` in `DesktopCompositionExtensions`): registers shared contexts (AppAuth/Llm/Notes/Automation/ChatSocial/DeviceConnect/DeviceManagement) + shared AI tools (`AddSystemTools().AddApkParserTools().AddNotesTools()`) + `AddCustomAgents()`. Automation has no AI adapter; instead `AddDesktopCommon` registers `IAgentExecutor→ConversationBackedAgentExecutor` (host impl of the business port). Each tenant host appends its own contexts + tools. Key desktop-specific registrations: `IChatClientFactory→DesktopChatClientFactory`, `IModelSource→LlmConfigModelSource`, `IConversationStore→SqliteConversationStore`, `IPreferencesStore→JsonPreferencesStore`, `IUiDispatcher→AvaloniaDispatcher`, `IConnectorViewModel→CtpConnectorViewModel`/`QifuConnectorViewModel`.

### Experts page (3 tabs)

- **专家 (Agent)**: custom agent cards (from `ICustomAgentCatalog`), CRUD via `ExpertEditViewModel`, summon to chat.
- **连接器 (Connector)**: tenant-specific `IConnectorViewModel` via `ContentControl` + DataTemplate. Stockapp: CTP card with "连接设置" dialog (`CtpLoginWindow`) + auto-login. Qifu: 毓数平台 (browser OAuth) + 云文档 (Playwright SSO), login + sync-to-server.
- **市场 (Market)**: `IAgentMarketplaceService` cloud catalog (browse/summon/publish/update/unpublish). Gated on app login (`CanShowMarket`). Server endpoint `api/app/agent-marketplace/*`.

## Non-obvious gotchas

- **ABP auth `scope`**: login MUST send `scope = "Abp alphaagent_chat"` (set in `AddDesktopCommon`). Without it, the token lacks the `Abp` API audience → `401 Bearer error="invalid_token"` on all protected endpoints. Refresh does NOT need scope (refresh token preserves scopes).
- **`TenantHeaderDelegatingHandler`**: registered on `IAppServerHttpClient` ahead of `BearerTokenDelegatingHandler`. Sends `__tenant` header (tenant name from `AppServerOptions.Tenant`). `AppServerOptions` is a **plain singleton** (not `IOptions`), so inject it directly — `IOptions<AppServerOptions>` returns a default empty instance.
- **`IAppServerHttpClient.PostAsync<T>` has `where T : class`** — can't use `Guid?`/`bool`. Use `PostRawAsync` + manual parse for value-type responses.
- **`AgentSourceParser.Serialize` signature**: 12 params (name/description/category/tools/memory/maxHistory/model/source/marketplaceId/marketplaceSyncedAt/displayName/prompt). Changing it requires updating all callers.
- **Markdown rendering**: `Markdown.Avalonia.Tight 12.0.0-a3` in `Acme.Desktop.Shared`. `SelectableMarkdownViewer` (custom subclass of `MarkdownScrollViewer`) for selectable copy (Ctrl+C/Cmd+C). SharedTheme.axaml has code-block font fallback styles to avoid macOS Consolas crash.
- **Lean backtest finalization quirks**: `RunAsync` saves/restores `Console.Out`/`Console.Error`; `resultHandler.StatisticsResults()` wrapped in try/catch (`.First()`-on-empty on sparse equity curves).
- **`SqliteQuoteRepository.ReadAllAsync`** reads columns by ordinal — freq is ordinal 1, date is ordinal 2. Don't swap.
- **`Securities` naming quirk**: namespace `Acme.Securities.*` (plural), DI methods `AddSecurity*` (singular).
- **CTP native libs**: `externals/Ctp.Net.Core` P/Invokes `libctp_native`; prebuilt `.dylib/.so/.dll` ship as Content. CTP auto-login requires saved config + `AutoLogin` flag.
- **Emoji icons**: `SharedTheme.axaml` has `MinHeight` on `Button.settings-nav`/`nav-rail`/`top-tab` to prevent emoji clipping at high DPI.
- **Automation (定时任务)**: scheduler is a process-internal 1-min polling `Timer` — fires **only while the app is open** (no cron/Quartz/backfill). Started fire-and-forget from `MainViewModelBase.InitializeAsync`; `AutomationScheduler.StartAsync` has a double-start guard. Each `AutomationTask` owns a stable `ConversationId` (assigned on create) so all its fires thread into one chat conversation via `ConversationBackedAgentExecutor`. `IAutomationScheduler.TaskCompleted` is subscribed by both `AutomationViewModel` (refresh task cards) and `MainViewModelBase` (refresh 最近会话). The scheduler calls `IAgentExecutor` (business port) — must be registered at the composition root or DI resolution of `IAutomationService`/`IAutomationScheduler` throws.
- When adding a new context/adapter, also add its projects to `Aether.AI.Platform.slnx` (folder + `<Project Path=...>`). Not every context needs an `ai/` adapter — if business only needs to *invoke* AI (not expose tools to the agent), use the [business-port pattern](#business-domain-port--host-implementation) instead and skip the adapter.
