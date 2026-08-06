# 桌面应用控制：MCP 路线设计方案

> 状态：Windows 客户端侧已落并**端到端验证跑通**（2026-08）。框架 + router + 选择器 + 真 `IMcpToolInvoker`（独立连 FlaUI-MCP）+
> locator↔ref 映射层（含 handle 跟踪）+ 真实 locator（用 snapshot 在同花顺上校准）+ 持仓解析 + DI 接线 + `net10.0-windows` 编译就位。
> FlaUI-MCP 已克隆到 `externals/FlaUI-MCP`（submodule）。已实测：连 server→枚举窗口→聚焦同花顺→点买入 tab→snapshot 下单表单全通。
> 剩余：下单回执对话框、持仓面板文本解析的 locator 需在真机校准。
> 范围：把 StockTrading（同花顺下单）的 OS 专属 GUI 自动化从 Aether 进程内下沉到进程外的
> **桌面控制 MCP server**，使 Aether 上层经标准协议调用、平台差异 = 不同 MCP 配置。

## 1. 背景与目标

现状：`Acme.StockTrading.Infrastructure`（`net10.0-windows` + `UseWPF` + FlaUI）在进程内操控同花顺下单
客户端 GUI。为此 Stockapp 多目标 `net10.0-windows;net10.0` + `#if WINDOWS` + 条件引用 +
`UnsupportedStockBrokerage` 桩 + `EnableWindowsTargeting` 兜底（见 CLAUDE.md「Multi-target host」）。

目标（按「现状先不变 + 多一个 MCP 实现」的决策调整）：

- **多一个 MCP 的 `IStockBrokerage` 实现**：与既有 FlaUI 进程内实现**并列**，不替换、不删多目标。
  经 `RoutingStockBrokerage` 运行时切换（股票页选择器），默认仍走 FlaUI，现状行为不变。
- **StockTrading 零改动**：不在 StockTrading 内加项目；`IStockBrokerage` 的新实现在独立 bridge 项目。
- **OS 专属下沉**：FlaUI（Win）/ AX·osascript（Mac）自动化搬进独立进程（MCP server），Aether 经标准协议调用。
  （未来若 MCP 路线完全验证通过、取代 FlaUI，再考虑 Stockapp 退回单一 `net10.0`、删多目标——属后续可选步骤，不在本期。）
- **固定 vs LLM 分家**：「固定过程」（固化操作某 app 的 UI 流程）是独立 business context；「LLM 直接调 MCP」（不固定）走 agent 路径。两者共用同一 MCP server。

## 2. 架构选型（塌缩后的两种）

本质只有两种架构，不是三种：

| 架构 | 形态 | 优 | 劣 |
|------|------|----|----|
| **进程内** | Aether ──P/Invoke──▶ 原生 `.dll/.dylib/.so`（C++ 调 UIAutomation/AX/AT-SPI） | 自包含、无运行时依赖、可给任意语言用 | C++ 写 UI 自动化比 FlaUI 痛苦；无现成生态 |
| **进程外 + IPC** | Aether ──IPC──▶ driver 进程；协议 = MCP 或自定义 | 保留 FlaUI 易用；协议标准化后有现成 client/server | 多一个进程边界；每原语一次 IPC 往返 |

> 「named pipe / HTTP」是**传输**，「MCP」是跑在传输上的**协议**（JSON-RPC + 工具 schema，常骑 stdio
> 或 HTTP+SSE）。二者不同层，但扮演同一角色（Aether↔driver 的通道）。所以「IPC 驱动」与「MCP」是
> **同一种架构**，区别只是线上用标准协议（MCP）还是自造协议。

**选「进程外 + MCP 协议」**：Aether 已是 MCP 客户端（`Aether.AI.Mcp` + `McpServerRegistry`），Windows
侧用现成 [FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP) server（已克隆到 `externals/FlaUI-MCP`，`net8.0-windows`，
ref 驱动仿 Playwright），Mac 侧自建。零原生代码、协议标准化、天然 LLM 可调。

## 3. 两条路径，共用一个 MCP server

```
┌────────────────────────────────────────────────────────────────┐
│  Aether 上层（纯 net10.0，OS 无关，零 #if）                       │
│                                                                │
│  ① 固定过程路径                    ② LLM 直连路径（非固定）        │
│  ┌──────────────────────┐         ┌──────────────────────┐     │
│  │ business/AppAutomation │         │ agent（Aether 既有     │     │
│  │  IProcedureRunner      │         │  MCP client）         │     │
│  │  RunAsync("ths_place_  │         │  直接调 Click/Type/…  │     │
│  │   order", …)           │         │  序列由 LLM 自决       │     │
│  │  └ IDesktopAutomation  │         └──────────┬───────────┘     │
│  │     (McpDesktopAuto…)  │                    │                 │
│  └──────────┬─────────────┘                    │                 │
│             │ host 适配器                        │                 │
│             │ IStockBrokerage → IProcedureRunner │                 │
│  ┌──────────▼──────────┐                        │                 │
│  │ StockTrading（零改动）│                        │                 │
│  │  IStockBrokerage 端口 │                        │                 │
│  └─────────────────────┘                        │                 │
└──────────────────────────────────────────────────┼─────────────────┘
                                                   │ MCP（JSON-RPC over stdio/HTTP）
                              ┌────────────────────┴────────────────────┐
                              ▼                                          ▼
                    Windows: FlaUI-MCP server              macOS: 自建 Mac-MCP server
                    (net10.0-windows, FlaUI)               (osascript/AX → MCP 工具)
                              │                                          │
                              ▼                                          ▼
                    UIAutomation COM                        Accessibility AX API
                              │                                          │
                              ▼                                          ▼
                    同花顺下单客户端（Win）                   同花顺 Mac 客户端 / 任意 Mac app
```

**关键：平台差异 = 一份 MCP server 配置**，Aether 代码完全一致：

```jsonc
// Windows: mcp-servers/desktop.json
{ "mcpServers": { "desktop": { "command": "dotnet", "args": ["run","--project","FlaUI-MCP"] } } }
// macOS:    mcp-servers/desktop.json  → 指向自建 mac-mcp-server
```

## 4. 「固定过程」vs「LLM 直连」——为什么分家

| | ① 固定过程（新 business context `AppAutomation`） | ② LLM 直连（agent 路径） |
|---|---|---|
| 谁决定序列 | C# 代码里固化 | LLM 运行时自决 |
| 形态 | `IProcedureRunner.RunAsync(name, args)` | agent 调 MCP 工具（Aether 既有 MCP client） |
| 可靠性 | 确定性强，适合精确/不可逆动作（下单） | 灵活但可能误点，适合探索性操控 |
| 用 C# 原语抽象？ | 是（`IDesktopAutomation`，代码里调原语） | 否（agent 直接调 MCP 工具，不经过 `IDesktopAutomation`） |
| 耦合业务端口？ | 经 host 适配器映射（如 `IStockBrokerage`） | 不耦合（agent 被赋予工具即可） |

**`IDesktopAutomation`（Click/Type/GetUITree 等 C# 原语抽象）只服务于①**——固定过程在代码里调原语。
②不需要它，agent 直接调 MCP server 的工具。两者共用同一个 MCP server（底层 Click/Type/GetUITree 工具）。

> 「固定过程」本质是把操作某 app 的 UI 流程**固化成代码**——今天固化「操作同花顺下单」，未来可固化
> 「操作 app Y 做 Z」。它是一个独立、可复用的 business context，不属于 StockTrading。

### 4.1 ⚠️ 易踩坑：两条路径共用 MCP server，但上层客户端 seam 是分开的

两条路径在 **server 端**（FlaUI-MCP / Mac-MCP）共用同一组工具，但在 **Aether 上层**走的是**两套独立、互不相连**的客户端 seam。这是设计有意为之，但容易误以为「配个 `desktop.json` 就两条都通了」——**不是**。

| | ② LLM 直连路径 | ① 固定过程路径 |
|---|---|---|
| Aether 侧 seam | `McpServerRegistry`（`Aether.AI.Mcp`） | `IMcpToolInvoker`（`AppAutomation.Domain`） |
| 怎么拿工具 | `StartAsync()` 连 server → `GetTools()` 返回 `AIFunction[]` → 进 `ToolRegistry` → agent | `CallToolAsync(toolName, args)` 按固定名字直调 |
| 谁触发调用 | LLM 运行时自决 | C# 代码（`ThsPlaceOrderProcedure` 等） |
| 配 `mcp-servers/desktop.json` 就能用？ | **能**——agent 自动获得 click/type 工具（既有行为，与 AppAutomation 无关） | **不能**——`IMcpToolInvoker` 与 `McpServerRegistry` 没接 |
| 现状 | 真实可用 | 桩，fail-fast（见下） |

**关键事实**：`AppAutomation` **不引用** `Aether.AI.Mcp`（grep 确认零引用），`IMcpToolInvoker` 是 AppAutomation 自己的端口，目前**唯一实现是 `NotConnectedMcpToolInvoker` 桩**。代码链：

- `AddAppAutomation()` **故意不注册** `IMcpToolInvoker`（[AppAutomationServiceCollectionExtensions.cs:10-11](../business/AppAutomation/Acme.AppAutomation.Infrastructure/AppAutomationServiceCollectionExtensions.cs#L10-L11)）——它要包 `McpClient`（alpha 包，隔离在 `Aether.AI.Mcp`），由宿主给。
- 宿主给的是桩：[App.axaml.cs:112](../apps/Stockapp/Acme.Stockapp.Desktop/App.axaml.cs#L112) `services.AddSingleton<IMcpToolInvoker, NotConnectedMcpToolInvoker>()`；桩 `IsConnected => false`、`CallToolAsync => "{}"`（[NotConnectedMcpToolInvoker.cs:13-15](../business/AppAutomation/Acme.AppAutomation.Infrastructure/NotConnectedMcpToolInvoker.cs#L13-L15)）。
- 桩让 `McpDesktopAutomation.IsConnected` 恒 false（[McpDesktopAutomation.cs:23](../business/AppAutomation/Acme.AppAutomation.Infrastructure/McpDesktopAutomation.cs#L23)），`ProcedureRunner` 守卫直接返回 `Fail("桌面控制后端未连接（MCP server 未就绪）")`（[ProcedureRunner.cs:35-36](../business/AppAutomation/Acme.AppAutomation.Application/ProcedureRunner.cs#L35-L36)），过程体根本不执行。

**因此**：Mac 上股票页切到 MCP、点下单 → 优雅失败「桌面控制后端未连接」，**不崩但什么都做不了**。这是有意为之（Mac 无 FlaUI、无 MCP server），不是 bug。而且**就算现在往 `mcp-servers/` 放个 desktop server 配置**，也只让②LLM 路径活（agent 多了桌面工具），**救不了①固定过程路径**——后者缺的是真实 `IMcpToolInvoker`，不是 server 配置。

> Windows 期「真 `IMcpToolInvoker`」已选定 **(a) 独立连**（见 §10）：`McpDesktopToolInvoker`（`Acme.AppAutomation.McpClient` 项目）
> 自己 `McpClient.CreateAsync` 连 desktop server（FlaUI-MCP），与 `McpServerRegistry` 各连各的。简单自包含；同进程可能连两次同一 server
> （① 固定过程 + ② LLM 各一条连接），可接受——两路径独立故障域。`McpDesktopAutomation` 是 locator↔FlaUI-MCP ref 模型的映射层
> （§5.1）：不直接调假设的 `click`/`type`，而是 `windows_snapshot` 找 ref → `windows_click`/`windows_type`，对齐 server 真实工具名。

## 5. 桌面控制原语（`IDesktopAutomation` / MCP 工具）

三种边界（进程内 / IPC / MCP）共用同一套原语；走 MCP 时就是 MCP server 暴露的工具列表：

```
窗口  ListWindows() / FocusWindow(id) / GetActiveWindow()
观察  GetUITree(scope)          ← accessibility 树 = 桌面 DOM，喂 LLM 比截图可靠
      Screenshot(scope)
      FindElement(locator)      ← locator = by Name / Role / AutomationId / 坐标
操作  Click(locator) / DoubleClick / RightClick
      Type(text) / Key(combo)   ← Mac 上 combo = "command+a"，Win 上 "ctrl+a"
      Scroll(locator, delta)
等待  WaitForElement(locator, timeout) / Sleep(ms)
读取  GetText(locator) / GetValue(locator)
```

> `GetUITree` 是整套设计里最值钱的原语——accessibility 树本质是「桌面的 DOM」，把它作为结构化
> 原语暴露给上层，比纯截图驱动 LLM 可靠得多，是当下 GUI-agent 的最佳实践（结构化树 + 视觉混合）。

### 5.1 FlaUI-MCP 的 ref 模型与 locator 映射层

FlaUI-MCP 是 **ref 驱动**（仿 Playwright）：工具名 `windows_*`，交互前必须先 `windows_snapshot` 取 accessibility 树
（每行 `- role "name" [ref=wXeY]`），再用 ref 调 `windows_click`/`windows_type`/`windows_get_text`。
这和上层 `IDesktopAutomation` 的 `DesktopLocator`（ByName/ByRole）模型不同——**`McpDesktopAutomation`
是两者间的映射层**：

| `IDesktopAutomation` 原语 | FlaUI-MCP 工具 | 映射 |
|---|---|---|
| `FocusWindow(title)` | `windows_focus` {title} | 直传 |
| `WindowExists(title)` | `windows_list_windows` | 输出里按标题匹配 |
| `Click/DoubleClick/RightClick(locator)` | `windows_click` {ref,button?,doubleClick?} | snapshot→树里按 Name/Role 找 ref→click |
| `Type(text)` | `windows_type` {text} | 直传（打到聚焦元素） |
| `Key(combo)` | `windows_send_keys` {chord} | combo `ctrl+a`→chord `Ctrl+A`、`tab`→`Tab` |
| `GetText(locator)` | `windows_get_text` {ref} | snapshot→找 ref→get_text |
| `GetUITree(scope)` | `windows_snapshot` {handle?} | 直传 |
| `WaitForElement(locator,timeout)` | 轮询 snapshot | 找到 ref 即 true |
| `Sleep(ms)` | 本地 `Task.Delay` | 不走 server |

`McpDesktopAutomation.FindRefInTree`（纯函数，单测覆盖）在 snapshot 文本里按行匹配 `- role "name" [ref=...]`：
ByName 匹配 `"name"`、ByRole 匹配行首 role（role 名对齐 FlaUI-MCP 的 `GetElementRole`：Edit→`textbox`、Button→`button`、TabItem→`treeitem`…）。
`ToChord` 把小写 combo 转成 Playwright 风格 chord。这两个纯函数 + 映射逻辑全可单测（用 fake invoker 喂树文本），无需真 server。

**handle 跟踪**（实测踩坑）：FlaUI-MCP 的 handle 是 server 进程内自增计数器（`wN`），`windows_snapshot` 不带 `handle` 时取「最近 launched」窗口（常错）。
故 `FocusWindowAsync` 从 `windows_focus` 返回文本 `(handle: w5)` 里正则提取 handle 存 `_activeHandle`，后续 snapshot/click 都带上它——否则会 snapshot 到错的窗口。

**TFM 踩坑**：FlaUI-MCP 原是 `net8.0-windows`，本机只装 .NET 10 → 启动即 `IOException: server shut down unexpectedly`。
submodule 内 csproj 改成 `net10.0-windows` + `<RollForward>LatestMajor</RollForward>`，用本机 .NET 10 直接跑（FlaUI 5.0 兼容）。

## 6. 项目结构（已落地）

```
business/AppAutomation/                          ← 新 bounded context：固定化桌面操作流程（① 固定过程）
  Acme.AppAutomation.Domain/                     ← IProcedureRunner / IDesktopAutomation / IMcpToolInvoker 端口 + 模型（✅ 已落）
  Acme.AppAutomation.Application/                ← Procedure / ProcedureCatalog / ProcedureRunner（✅ 已落）
  Acme.AppAutomation.Infrastructure/             ← McpDesktopAutomation（原语→MCP 工具映射，✅ 已落）
  │                                                + NotConnectedMcpToolInvoker（Mac 无 server 桩，✅ 已落）
  │                                                + ThsTradingProcedures（同花顺过程，locator 占位，✅ 已落）
  │                                                + AddAppAutomation() DI（✅ 已落）
  Acme.AppAutomation.StockTradingBridge/         ← AppAutomationStockBrokerage : IStockBrokerage（✅ 已落，独立项目，不耦合 AppAutomation 通用层）
                                                  + ThsPositionsParser（持仓 Tab 文本 → StockPosition[]，✅ 已落）
  Acme.AppAutomation.McpClient/                  ← McpDesktopToolInvoker : IMcpToolInvoker（✅ 已落，隔离 ModelContextProtocol 1.2.0 alpha 包，独立连 desktop server）

apps/Stockapp/Acme.Stockapp.Desktop/
  Hosting/RoutingStockBrokerage.cs               ← 路由 IStockBrokerage：keyed 持有 FlaUI + MCP 两后端，运行时切换（✅ 已落）
  App.axaml.cs                                   ← keyed 注册两后端 + router = 默认 IStockBrokerage（✅ 已落）
  ViewModels/StockTradingViewModel.cs            ← 下单方式选择器 UseMcp（✅ 已落）
  Views/StockTradingView.axaml                   ← 状态条 FlaUI/MCP RadioButton（✅ 已落）
  mcp-servers/desktop.json                       ← 平台专属 MCP server 配置模板（✅ 已落，指向 externals/FlaUI-MCP；复制到 tenant 目录即启用）

externals/FlaUI-MCP/                             ← 第三方 MCP server（✅ 已克隆，net8.0-windows，ref 驱动；类比 externals/Ctp.Net.Core / Lean）

（② LLM 直连路径：已有，无需新项目）
  Aether agent → Aether.AI.Mcp（既有 client）→ desktop MCP server 工具

（StockTrading 全部不变）
  business/StockTrading/Acme.StockTrading.{Domain,Application,Infrastructure}  ← 一行不改
  Infrastructure（FlaUI）保留，仍按现状注册为 keyed "flaui" 后端
```

**为什么这样分**：

- `AppAutomation` **通用、不耦合 StockTrading**——它只知道「过程名 + 参数 → 跑一组原语」。同花顺下单是
  其中一个过程定义。未来加「操作 app Y」= 加个过程定义，不动 AppAutomation 核心，也不碰 StockTrading。
- `IStockBrokerage`（StockTrading 端口）的 MCP 映射在 **独立 bridge 项目** `AppAutomation.StockTradingBridge`
  （`AppAutomationStockBrokerage`），引用 `StockTrading.Domain`（外域契约）+ `AppAutomation.Application`。
  StockTrading 真正零改动。
- `RoutingStockBrokerage` 在 Stockapp 组合根——business port → host impl 模式（类比 `IAgentExecutor`），
  把「选哪个后端」这件跨上下文的事留在宿主。
- `IDesktopAutomation` + `McpDesktopAutomation` 在 AppAutomation 内——它是①固定过程调 MCP server 的
  C# 客户端；② LLM 路径不经它。

> `IDesktopAutomation` 初始放 AppAutomation.Domain/Infrastructure；当出现第 2 个消费者时再抽到共享库
> （`externals/AppControl.Net.Core` 之类），接口稳定无需重写。

## 7. 迁移进度（Mac 已落 / Windows 剩余）

**Mac 已落（编译通过 + 14 单测通过）**：

1. ✅ `business/AppAutomation/{Domain,Application,Infrastructure}` 三件套 + `StockTradingBridge`。
2. ✅ `McpDesktopAutomation`（原语→`IMcpToolInvoker` 工具调用映射）+ 同花顺过程（locator 占位）。
3. ✅ `AppAutomationStockBrokerage : IStockBrokerage`（端口→过程映射）。
4. ✅ `RoutingStockBrokerage` + 股票页选择器 + keyed DI 接线。Mac 上两后端皆惰性（FlaUI 桩 + `NotConnectedMcpToolInvoker` 桩），选择器可切可持久化，操作优雅失败。
5. ✅ 14 单测（原语映射、过程编排序列、适配器映射），无需 server。

**Windows 剩余（切到 Windows 做）**：

1. ✅ `IMcpToolInvoker` 真实现：新建 `Acme.AppAutomation.McpClient` 项目（隔离 `ModelContextProtocol` 1.2.0 alpha 包），
   `McpDesktopToolInvoker` 自己 `McpClient.CreateAsync` 连 desktop server（**独立连**，§4.1 开放问题已定），按工具名直调。
   `App.axaml.cs` 读 `<tenantDir>/mcp-servers/*.json` 找 `desktop` 条目 → 注册真 invoker；找不到/非 Windows 回退 `NotConnectedMcpToolInvoker` 桩。
2. ✅ `ThsLocators` 真实 locator：照搬已验证参考 `FlaUiStockBrokerage`——导航 `买入[F1]`/`卖出[F2]`/`撤单[F3]`、提交按钮 `买入`/`卖出`、
   3 个表单 Edit 用点代码框 + Tab 跳转序列（不依赖逐个 Edit locator）。`SymbolField`/`Receipt` 等仍需接 FlaUI-MCP 后用 `get_ui_tree` 校准。
3. ✅ `mcp-servers/desktop.json` 模板：仓库 `apps/Stockapp/.../mcp-servers/desktop.json` + `README.md`（指向 `externals/FlaUI-MCP`，复制到 tenant 目录即启用）。FlaUI-MCP 已克隆到 `externals/FlaUI-MCP`（`net8.0-windows`，编译通过）。
4. ✅ `QueryPositions` 持仓解析：`ThsPositionsParser`（移植参考 12 列 Tab 解析）放 bridge，`QueryPositionsAsync` 用它解析过程返回的面板文本。
5. ⏳ 端到端验证：选 MCP → 连接 → 下单 → `IStockBrokerage → router → AppAutomationStockBrokerage → 过程 → MCP server → 同花顺`。
   需本地 GUI + 外部 FlaUI-MCP server，本环境无 GUI，留本地验证清单（见 `mcp-servers/README.md`）。
6. ✅ `net10.0-windows` TFM 编译通过（`#if WINDOWS` 块 FlaUI keyed 注册 + 真 invoker 接线）。

> 注：本期**不**做「Stockapp 退回单一 net10.0 / 删多目标」——那是 MCP 路线完全验证通过、决定取代 FlaUI 后的可选后续步骤。

## 8. Mac vs Windows 工作分工

| | Mac（已完成） | Windows（剩余） |
|---|---|---|
| AppAutomation 框架骨架 | ✅ 接口 + 过程目录 + 同花顺编排骨架 + 录制 mock + 14 单测 | — |
| MCP 线客户端 | ✅ `McpDesktopAutomation`（原语↔工具映射，经 `IMcpToolInvoker` seam） | ✅ `McpDesktopToolInvoker`（包 `McpClient` 1.2.0，独立连） |
| 真实 UI 序列 | ✅ 编排骨架（locator 占位） | ✅ 填真实 locator（照搬参考；`SymbolField`/`Receipt` 留校准） |
| host 适配器（bridge） | ✅ `AppAutomationStockBrokerage` + 单测 | ✅ 接真 runner + 持仓解析（`ThsPositionsParser`） |
| Stockapp 接线 | ✅ router + 选择器 + keyed DI（多目标保留） | ✅ `net10.0-windows` 编译通过 + 真 invoker 接线 |
| `mcp-servers/desktop.json` | — | ✅ 模板 + README（指向 FlaUI-MCP） |
| Mac-MCP server | ⏸ 可另起：osascript/AX 包成 MCP 工具（独立项目） | — |
| FlaUI-MCP | — | ⏳ 直接用现成 server |

**Mac 已落的核心**：框架 + router + 选择器 + 桩，编译通过、单测通过、选择器可切可持久化。Mac 上两后端皆惰性
（无 FlaUI、无 MCP server），操作优雅失败。**Windows 切过去后**：换真 `IMcpToolInvoker` + 填真实 locator +
加 `desktop.json` → 端到端跑通，Mac 已落的代码不用改。

## 9. 代价与注意

- **MCP server 是独立进程**：用户机器需能跑（Windows 上 FlaUI-MCP 要 `dotnet run` 或发布成 exe）。
  部署上 Aether 不再自带 FlaUI，改依赖外部 MCP server——分离更干净，但多了「用户得有这个 server」的前提。
- **Mac 无现成 MCP server**：需自建（osascript/AX 包成 MCP 工具）。Windows 白嫖 FlaUI-MCP，Mac 工作量省不掉。
- **协议开销**：固定过程里每个 Click/Type 都一次 JSON-RPC 往返。本地下可接受，高频调用注意。
- **精确交易别交 LLM**：下单走①固定过程；②LLM agent 留给探索性操控（见第 4 节）。
- **Accessibility 权限（Mac）**：Mac 上控制其它 app UI 需「辅助功能」权限 + 正确签名，首次运行要引导授权。
- **元素 locator 脆性**：同花顺 UI 改版会使 locator 失效；`GetUITree` + 按 Name/Role 定位比坐标稳。

## 10. 待定 / 开放问题

- **MCP SDK 选型**：已定——用 `ModelContextProtocol`（官方 C# SDK）1.2.0（与 `Aether.AI.Mcp` → `Microsoft.Agents.AI.Mcp` 传递依赖同版本），隔离在 `Acme.AppAutomation.McpClient`。`McpClient.CreateAsync` + `CallToolAsync(name, args, progress, ct)` → `CallToolResult.Content`（`TextContentBlock.Text`）。
- **真实 `IMcpToolInvoker` 怎么连**（见 §4.1）：已定 **(a) 独立连**——`McpDesktopToolInvoker` 自带 `McpClient` 连 desktop server，不复用 `McpServerRegistry`。简单自包含；同进程连两次同一 server 可接受。
- **过程定义的载体**：代码里硬编码序列 vs 数据化（YAML/JSON 描述过程步骤）。初期代码硬编码最直接；
  过程多了再数据化（甚至让 AI 生成过程定义）。
- **`IStockTradingOptions` 演进**：MCP 路线下不再需要 `ClientExecutablePath`，改为 MCP server 命令/端点。
  可扩 `IStockTradingOptions` 或新建 `IStockTradingMcpOptions`（host 适配器侧）。
- **Mac-MCP server 是否纳入本仓库**：建议独立项目/仓库（类比 `externals/Ctp.Net.Core`），Aether 只消费。
- **FlaUI Infrastructure 去留**：MCP 路线验证通过后归档/删除；验证前保留不动（Stockapp 不引用）。

## 11. 与 Ctp.Net.Core 的类比

| | Ctp.Net.Core | 本方案 |
|---|---|---|
| OS 专属在哪 | `libctp_native.dll/.dylib`（C++ 包 CTP SDK） | MCP server 进程（FlaUI / AX） |
| Aether 怎么用 | P/Invoke + 托管包装 | MCP 客户端 + `IDesktopAutomation`（固定过程）/ agent 直调（LLM） |
| 平台差异 | 不同 RID 的原生库 | 不同 `mcp-servers/desktop.json` |
| 上层 | OS 无关 | OS 无关、零 `#if` |

不同点：CTP 有厂商 C SDK 可包；「控制 app」没有，得在 server 端自己实现自动化（FlaUI/AX）。所以
MCP server = 「`libctp_native` 的等价物」+「自动化的实现载体」二合一。
