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
dotnet test tests/Acme.Trading.Domain.Tests/Acme.Trading.Domain.Tests.csproj --filter "FullyQualifiedName~SmaCross"

# Run the sample console (composes all contexts). Without an LLM API key it runs an offline smoke check.
dotnet run --project apps/Aether.AI.Sample.Console
```

The first build after init is slow (Lean source compiles). Lean's transitive deps emit NU1902/3/4 vulnerability advisories (System.Drawing.Common / WCF / WinHttpHandler) — advisory only, expected.

## Big-picture architecture

A multi-context DDD platform built on **Microsoft Agent Framework** (`Microsoft.Agents.AI`, wrapped by `src/Aether.AI.Harness`). Top-level layout enforces a strict layering:

- `src/` — the **Aether.AI platform** itself, standalone. `Aether.AI.Core` (framework-agnostic domain, zero deps), `Aether.AI.Harness` (AgentFactory/HarnessRuntime/SessionManager/ToolRegistry wrapping MEAI), `Aether.AI.Memory`, `Aether.AI.Hosting` (`services.AddAetherAI()` DI entry point). Platform source never references `business/` or `ai/`.
- `business/` — **pure DDD bounded contexts**, each `Acme.<Context>.{Domain,Application,Infrastructure}`. AI-free.
- `ai/` — **business AI adapters** bridging Aether and each context. Peers of `business/`, kept separate so business stays AI-free.
- `externals/` — third-party engines: `Ctp.Net.Core` (native CTP wrapper) and `Lean` (QuantConnect Lean, git submodule).
- `apps/` — consumers/hosts (currently `Aether.AI.Sample.Console`). Composition root.
- `tests/` — all test projects live here, never interleaved with source.

### DDD layering rules (enforced; verify before changing csprojs)

- **Domain** = pure: zero `ProjectReference`, no SDK/HTTP/SQLite/logging packages, no `Aether.AI.*`. Entities/value-objects/ports only. A Domain may reference another context's Domain (shared kernel) — e.g. 8 contexts reference `Acme.AppAuth.Domain`.
- **Application** → Domain (+ foreign Domain port). Never Infrastructure/AI/externals.
- **Infrastructure** → Domain (+ externals + foreign Domain contract). Never another context's Infrastructure.
- **`business/` must NEVER reference `Aether.AI.*`.** The one past violation (`Automation.Infrastructure → Aether.AI.Harness`) was fixed by moving the AI-coupled code into `ai/Automation`.
- Dependency graph is a DAG; no cycles. `Acme.AppAuth.Domain` is the pure source (foundation), `Acme.Trading.Runtime.Lean` the pure sink.

### AI adapter convention (`ai/<Context>/Acme.<Context>.AI`)

Every adapter is the same shape (mirror `ai/Ctp`):
- csproj: `Aether.AI.Hosting` + the context's **Application** project (never Infrastructure/Domain directly).
- `XxxAIBuilderExtensions.cs` at project root: `AddXxxTools(this AetherAIBuilder)` (registers tools only) + `AddXxx(this AetherAIBuilder, string modelId)` (tools + agent). `ToolNames[]`, `CreateTool<T>` helper using `AIFunctionFactory.Create`.
- Tool classes live in a `Tools/` subfolder, namespace `Acme.<Context>.AI.Tools` (all 8 adapters follow this). The builder extension adds `using Acme.<Context>.AI.Tools;`. Non-tool adapters (e.g. Quotes' `SecurityLookupAdapter` ACL bridge) stay at the project root.
- Tool classes inject the context's **Application** service (`IXxxService`), with `[Description]`-annotated methods. Tools call Application contracts, never Domain/Infrastructure.
- **`AddXxxTools` registers TOOLS ONLY — it must NOT register business layers.** The host composition root wires business layers (`AddXxxInfrastructure` + `AddXxxApplication`). Tests using `AddXxx(modelId)` register business layers themselves (see `tests/Acme.Ctp.AI.Tests`).
- MEAI `AIFunctionFactory.Create` **strips the `Async` suffix** from method names — a method `QueryAccountAsync` becomes tool `QueryAccount`. `ToolNames` uses the stripped names.

### Foundation context

`business/AppAuth/` (`Acme.AppAuth.*`, DI classes named `Platform*Extensions`) is the **foundation**: app-server (ABP) OAuth2 auth + authenticated HTTP + `ICurrentUser`. Other contexts (Qifu, Llm, Securities, Notes, etc.) consume its `ICurrentUser` / `IAppServerHttpClient` / `IAppTokenStore` ports — never the reverse. CTP and Quotes are self-contained (no AppAuth dep).

### Trading + Lean (the engine-bridge pattern)

Two contexts implement the architecture doc "Trading is DDD business, Trading.Runtime is the bridge, Lean is the engine (don't reimplement)":
- `business/Trading/` — pure DDD: `Acme.Trading.Domain` (`IStrategy`/`MarketBar`/`SignalDirection`/`StrategyRegistry`/`BacktestResult`, strategies) + `Acme.Trading.Application` (orchestration, depends on the Runtime abstraction only).
- `business/Trading.Runtime/` — the bridge: `Acme.Trading.Runtime.Domain` (abstraction ports `IBacktestRuntime`/`ILiveTradingRuntime`, Lean-neutral DTOs) + `Acme.Trading.Runtime.Lean` (the ONLY project referencing `externals/Lean`; all `QuantConnect.*` types confined here). Runtime.Lean → Quotes.Domain (history) + Ctp.Domain (live session), matching the doc's diagram.

`externals/Lean` is consumed by `ProjectReference` to specific subprojects (Engine/Algorithm/Brokerages/Configuration/Logging/Common/Queues/Messaging). The Lean backtest runs in-process; the `backtest` command in the sample console exercises it end-to-end.

### Host composition (`apps/Aether.AI.Sample.Console/Program.cs`)

`ComposeContexts` registers every composed context's Infrastructure + Application layers; `RegisterAgents` chains `AddXxxTools()` for each adapter and builds one aggregate `assistant` agent with all tools. Persistence (SQLite paths) is chosen at the host. Several contexts (Automation, ChatSocial, DeviceConnect, etc.) are dormant — built but not wired in this host.

## Non-obvious gotchas

- **Lean backtest finalization quirks** (in `LeanBacktestRunner`): Lean's setup redirects `Console.Out` to capture algorithm output — `RunAsync` saves/restores `Console.Out`/`Console.Error` around the run or the host's post-backtest output is silently lost. Also `resultHandler.StatisticsResults()` is wrapped in try/catch because Lean's `ResultsAnalyzer.ReadEquityCurve` throws `.First()`-on-empty on sparse custom-data equity curves.
- **`SqliteQuoteRepository.ReadAllAsync`** reads columns by ordinal against `Columns = "security_id, freq, date, open, high, low, close, volume"` — freq is ordinal 1, date is ordinal 2. Don't swap them (a past bug crashed every cache-hit read with `DateTime.Parse("101")`).
- **`Securities` naming quirk**: project/namespace is `Acme.Securities.*` (plural; the entity type `Security` collides with a `System.Security`-ish namespace), but DI methods are `AddSecurityInfrastructure`/`AddSecurityApplication` (singular). The `Security` entity lives in namespace `Acme.Securities.Domain`.
- **CTP native libs**: `externals/Ctp.Net.Core` P/Invokes `libctp_native`; prebuilt `.dylib/.so/.dll` ship as Content. No C++ toolchain needed to build. Real CTP login needs `APP_BASEURL`/`APP_CLIENT_ID`/`APP_CLIENT_SECRET` env vars and live credentials.
- **Quotes `IndicatorCalculator`** needs `using Quote = Acme.Quotes.Domain.Quote;` (disambiguate from `Skender.Stock.Indicators.Quote`).
- When adding a new context/adapter, also add its projects to `Aether.AI.Platform.slnx` (folder + `<Project Path=...>`).
