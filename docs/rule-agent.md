# Rule Agent：无 LLM 的规则大脑 agent

> 状态：1.0（零文件修改刺探）✅ 已完成；1.0-C（规则当工具）✅ 已完成；1.1（专家集成）✅ 已完成（代码+测试，待手动验证）。
> 目标：在 `src/` 平台层提供一个「大脑换成规则、不用 LLM」的 agent，**复用整套 agent 生态**
> （ToolRegistry / MCP / 流式 / 审批 / ask_user / 持久化 / skill / 长期记忆），只把「决定下一步」
> 从 LLM 换成确定性规则。规则有**两种 facade**：agent 形态（独立跑，无 LLM）+ 工具形态（LLM agent
> 调一次 `run_rule` 拿压缩结果，省 token——见 §7）。

## 1. 核心思路

rule agent = 一个普通 agent，唯一区别是「大脑怎么来」：

- 今天：`AgentFactory.Create` → `_chatClientFactory.Create(modelId).AsHarnessAgent(options)`（LLM 当大脑）。
- 规则：`AgentDefinition.EngineKind == Rule` → `AgentFactory` 走 `BuildRuleAgent` →
  `new RuleChatClient(brain).AsHarnessAgent(options)`（规则当大脑）。

下游一字不改：工具解析、skills、历史 provider、`HarnessRuntime.RunStreamingCoreAsync` 流式分发、
审批包装、ask_user、会话持久化——全部只认 `IChatClient` 流出的 `AIContent` + `ToolRegistry` 里的
`AIFunction`，不关心大脑是 LLM 还是规则。

**接缝就是 `IChatClient`**。证据：本项目已有 `ToolFilteringChatClient : IChatClient` 装饰器
（[ToolFilteringChatClient.cs](../src/Aether.AI.Harness/Agents/ToolFilteringChatClient.cs)）；
工具执行是 MAF 框架的活，不是 client 的（[HarnessRuntime.cs:312](../src/Aether.AI.Harness/HarnessRuntime.cs#L312)
只观察 `FunctionCallContent`/`FunctionResultContent` 分发到 UI，不 invoke）。规则大脑只管流出
`FunctionCallContent`/`TextContent`，执行、结果回填、循环全归框架。

## 2. 分层（守 DDD 红线）

| 层 | 内容 | 为什么在这 |
|---|---|---|
| `Aether.AI.Core` | `EngineKind` 枚举 + 规则 DTO（`RuleProgram`/`RuleStep`/`RuleMatch`/`RuleAction`） | 纯数据、零依赖、可序列化 |
| `Aether.AI.Harness` | `IRuleBrain`/`RuleContext` + `RuleProgramBrain`（求值）+ `RuleChatClient : IChatClient`（agent 形态引擎）+ `RuleToolExecutor`（工具形态自包含执行器）+ `RuleRegistry` + `RuleTool`（`run_rule` AIFunction）+ `AgentFactory.BuildRuleAgent` | 用 MEAI 的 `AIContent`/`ChatMessage`，Harness 本就引 |
| `Aether.AI.Hosting` | （1.1）DI 注册 | 对称 `ConfigureToolRegistry` |
| 专家编辑器 + `AgentSourceParser` | （1.1）`engine`/`rules` 字段 + 规则编辑区 | 持久化 + CRUD |
| 规则内容（程序/代码大脑） | 业务侧或租户数据，**不进 src/** | src/ 只放引擎；业务知识在外 |

## 3. 规则模型（声明式 DTO + 代码大脑双轨，一期只做声明式）

```csharp
// Core
public enum EngineKind { Llm, Rule }
public sealed class RuleProgram {
    public string Name { get; set; } = "";
    public List<RuleStep> Steps { get; set; } = new();
    public string? NoMatchReply { get; set; }   // 无命中兜底（终止）
    public int MaxSteps { get; set; } = 20;      // 死循环安全闸
}
public sealed class RuleStep { public string? Name; public RuleMatch Match; public RuleAction Action; }
public sealed class RuleMatch {                  // 全 null = always
    public string? OnUserRegex;    // 匹配本轮最新 user 消息（捕获组供 {user.N}）
    public string? OnResultRegex;  // 匹配最新工具结果文本
    public int? MinToolCalls;      // 本轮已调工具 ≥ N 才命中（步进）
    public int? MaxToolCalls;      // 本轮已调工具 ≤ N 才命中
}
public sealed class RuleAction {
    public string? Tool;           // 非空 → 流 FunctionCallContent（循环继续）
    public string? ArgsJson;       // 模板 {user.N} {result}
    public string? ReplyText;      // 非空 → 流 TextContent（终止循环），与 Tool 互斥
}
```

### 引擎语义（`RuleProgramBrain : IRuleBrain`，无状态）

每次 MAF 调 `RuleChatClient.GetStreamingResponseAsync(history, options)`：

1. 从 `history` 推导 `RuleContext`：本轮最新 user 消息、最新 `FunctionResultContent`、本轮已调工具次数
   （= 最后一条 user 消息之后的 `FunctionCallContent` 计数）。**不从外部存状态，全部 history 推导**
   → 天然支持持久化/重启/多会话隔离。
2. `Steps` 自上而下，首个 `Match` 命中的 `Action` 触发：
   - `Tool` 非空 → 流一个 `FunctionCallContent`（CallId + 模板替换后的 args）。MAF 执行 → 结果回填 → 再调引擎 → 步进。
   - `ReplyText` 非空 → 流 `TextContent`（终答），循环结束。
   - 两者皆空 → 穿透下一条。
3. 无命中 → 流 `NoMatchReply`（默认「无匹配规则」），终止。
4. `MaxSteps` 到 → 强制终止。

**保证收敛**（设计硬约束，否则 MAF 死循环）。固定流程 = `MinToolCalls` 步进；分支 = `OnResultRegex`；
路由 = `OnUserRegex`。模板（最小）：`{user.N}`（OnUserRegex 第 N 捕获组）、`{user}`、`{result}`。

### 代码大脑 `IRuleBrain`（留缝，一期不建注册机制）

```csharp
public interface IRuleBrain {
    ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext ctx, CancellationToken ct);
}
```

`RuleProgramBrain` 是其一实现。复杂逻辑（计算/循环/多步派生）写 C# `IRuleBrain` 注册进 `RuleRegistry`——
**一期只定义接口当扩展点，不做 `RuleRegistry`/`AddRuleBrain` 注册机制**（YAGNI，等真有需求再建）。

## 4. Skill / 长期记忆：对称白嫖（不 special-case）

`BuildRuleAgent` 逐行复用 `BuildHarnessAgent`——`ConfigureSkillsProvider` + memory provider 块原样保留。
不因「规则大脑读不了 NL」就砍掉，保对称性。

| 能力 | 规则大脑 | 说明 |
|---|---|---|
| skill `run_skill_script`/`read_skill_resource` | ✅ 活 | 脚本执行/文件 I/O，规则 step 可调 |
| skill `load_skill`（NL 指令进上下文） | ⚠️ 死但无害 | 注入 system prompt，规则大脑不读；无 LLM 不花 token |
| 长期记忆 store（沉淀 Q&A） | ✅ 活 | 不依赖大脑类型 |
| 长期记忆 recall（注入 NL） | ⚠️ 死但无害 | 同 skill，规则大脑不读 |

**判据**：规则大脑白嫖「工具执行面」（ToolRegistry/MCP/审批/ask_user/流式/持久化/skill 脚本/记忆 store），
白嫖不了「上下文注入面」（skill 指令/记忆 recall）——后者是给 LLM 读的 NL。`AgentModeProvider`（plan/execute）
不接——它是塑造 LLM 行为的，对规则无意义。

## 5. 复用清单

| 生态能力 | rule agent |
|---|---|
| ToolRegistry + AIFunction | ✅ |
| MCP 工具（McpServerRegistry） | ✅ |
| 流式 UI（tool/text 卡） | ✅ |
| 审批（Confirm/Dangerous） | ✅ 工具执行层，与大脑无关 |
| ask_user | ✅ |
| 会话持久化 | ✅ |
| skill | ✅ 脚本活、指令死、对称全接 |
| 长期记忆 | ✅ store 活、recall 死、对称全接 |
| Reasoning 卡 | ➖ 可选流 `TextReasoningContent` |

> 工具形态（接法 C，§7）额外省：业务工具不进 LLM 工具列表 → 每轮 system prompt 的工具 schema 体积缩小。

## 6. 两期落地

### 1.0 零文件修改刺探（纯新增文件，可整删）✅

验证「`IChatClient` 自己生成 `AIContent`、不委托真模型」这条路在 MAF HarnessAgent 下成立。全在新文件：
- Core：`EngineKind.cs`、`RuleProgram.cs`
- Harness：`RuleBrain.cs`（`IRuleBrain`+`RuleContext`）、`RuleProgramBrain.cs`、`RuleChatClient.cs`
- 测试：`RuleAgentTests.cs`（7 单测 + 1 `RuleChatClient.AsHarnessAgent` 端到端刺探，9/9 绿）

不通就删，现有代码一行没动。**已验证通过**（9/9）。关键坑：MAF `HarnessAgent` 默认开 `TodoProvider`，
会在真实 user 消息后注入第二条「### Current todo list」user 消息，污染 `RuleChatClient.BuildContext`
的 LatestUser；规则 agent 关 `DisableTodoProvider=true`（规则不消费注入式 NL 上下文）。

### 1.0-C 规则当工具（接法 C，省 token）✅

同一个 `RuleProgramBrain` 核心，套第二个 facade：`RuleToolExecutor`（自包含循环，内部执行工具、回填、
再决策到终止，返回一个字符串）→ `RuleTool.Create` 包成 `run_rule` `AIFunction` → `RuleRegistry` 按名分发。
LLM agent 调一次 `run_rule(name, input)` 拿压缩结果，中间工具调用/原始结果对 LLM 不可见。
- Harness 新增：`RuleToolExecutor.cs`、`RuleRegistry.cs`、`RuleTool.cs`
- 测试：`RuleToolTests.cs`（executor 单测 4 + RuleTool 单测 2 + 模拟 LLM agent 调 run_rule 端到端 1，7/7 绿）
- 详见 §7。**已验证通过**（7/7）。

### 1.1 加法式集成（专家列表 + 召唤 + 持久化 + run_rule 接线）✅

让 rule agent 进 app：专家列表能看到/编辑/召唤规则 agent，LLM agent 能调 `run_rule` 省 token。
每处都是加法 + 默认门控（`Engine` 默认 `Llm`），现有 agent 行为零影响。

**A. 数据 + 运行时（agent 形态核心）**

| 文件 | 改动 | 现有零影响 |
|---|---|---|
| `AgentDefinition`（Core） | +`Engine`(+`Rules`) 字段 | 默认 Llm |
| `AgentFactory.Create` | +1 早返 `if(Engine==Rule) BuildRuleAgent`（关所有注入式 provider） | 默认走原分支 |
| `CustomAgentDefinition`（Notes.Domain） | +`Engine`(string)/`RulesJson` | 默认 Llm，AI-free 存原串 |
| `AgentSourceParser` | +parse/serialize `engine`/`rules` | 仅非默认才写 key，YAML round-trip 不变 |
| `CustomAgentRunner.ToAgentDefinition` | +2 映射 | 现有不动 |

**B. UI 映射（专家编辑器）**

| 文件 | 改动 |
|---|---|
| `ExpertItem`（Shell） | +`Engine`/`RulesJson` observable |
| `ExpertEditViewModel` | +2 observable + `OnEngineChanged`→`IsRuleEngine` 刷新 + autosave + `BuildExpertItem` + `EngineOptions` |
| `ExpertsViewModel` | 3 处映射（load/save/existing）各 +2 行 |
| `ExpertEditView.axaml` | 「基本信息」加引擎 ComboBox（Llm/Rule）；「规则程序(JSON)」Expander（`IsVisible={Binding IsRuleEngine}`），多行 TextBox |

**C. 工具形态接线（C，省 token——LLM agent 调 run_rule）**

| 文件 | 改动 |
|---|---|
| `DesktopCompositionExtensions.AddDesktopCommon` | `services.AddSingleton<RuleRegistry>()` + `ConfigureToolRegistry` 注册 `run_rule`（Safe，toolResolver 闭包 ToolRegistry.ResolveAll） |
| `CustomAgentCatalog` | 注入 `RuleRegistry`；`ListAsync` 里每个 `Engine=="Rule"` 且 `RulesJson` 非空的 def 反序列化 + `_ruleRegistry.Register(name, program, desc)`（幂等覆盖） |

> 同一份规则程序两种 facade 自动共享：规则 agent 独立召唤（agent 形态）+ 自动进 `RuleRegistry` 供 LLM agent 经 `run_rule(name,input)` 调用（工具形态）。LLM agent 侧零改动——`run_rule` 进 ToolRegistry 后专家编辑器「内置工具」区自动列出，勾选即纳入 `definition.Tools`。

**测试**（全绿，0 新警告）
- `AgentFactoryRuleBranchTests`：`Create(Engine=Rule+Rules JSON)` → run → 断言工具被调 + 终答 `结果：echo:600000`（证明早返 + BuildRuleAgent + 关 TodoProvider 全通）。
- `AgentSourceParserRuleTests`：Rule round-trip 保 `engine`/`rules`；默认 Llm 不输出两 key；engine 大小写不敏感。
- 全测：`dotnet test Aether.AI.Platform.slnx` 全绿（含现有 16 条规则测试 + 新增 4 条）。

**待手动验证**（Stockapp）：agent 形态（新建规则 agent→召唤→查600000→终答正确）+ 工具形态（LLM agent 勾 run_rule→调规则→业务工具对 LLM 隐藏）。

### 不在一期（1.5+）

- 专家编辑器「规则程序」**表单 UI**（step 增删/排序、match/action 输入、EngineKind 切换隐藏 model/sampling）——1.1 用 JSON 文本框 + YAML 源码编辑兜底
- `IRuleBrain` **代码大脑**注册机制（`AddRuleBrain`；`RuleRegistry` 已用于规则程序的工具形态分发，但「代码大脑」按类型注册仍 YAGNI）
- 任意 scratch 变量（per-conversation 状态）
- 模板 JSON-path（`{result.field}`）
- Reasoning 卡

## 7. 接法 C：规则当工具（省 token）

规则的**两种 facade**，同一个 `RuleProgramBrain` 核心：

| facade | 壳 | 谁拥有循环 | 适用 |
|---|---|---|---|
| agent 形态 | `RuleChatClient` → MAF agent 循环 | MAF 框架 | 全程无 LLM（独立规则 agent） |
| 工具形态 | `RuleToolExecutor` → `run_rule` AIFunction | 执行器内部循环 | LLM 预取数据，省 token |

**工具形态省 token 三层**：
1. 省决策推理——「调哪个工具、参数、要不要再调」由规则确定性做完，LLM 不烧推理 token。
2. 省原始结果——工具的原始大返回在 executor 内部消费，LLM 只看 `run_rule` 返回的一个 compact 字符串。
3. 缩小 LLM 工具 schema——业务工具被 executor 在宿主侧调用（经 `toolResolver` 接 `ToolRegistry`），**不进 LLM 的工具列表**；LLM 工具池只多一个 `run_rule`，每轮 system prompt 体积随业务工具数缩小。

**为什么规则当工具是纯赚，而 LLM agent 当工具不一定省**：规则 executor 内部零 LLM 成本；LLM agent
当工具是把 outer 的 token 挪到 inner（inner 自己也烧 token），只有 inner 更便宜（小模型/受限工具/缓存）才净省。
（任意 `AIAgent` 机械上都能包成 `AIFunction`，但语义区别：Handoff=接力共享上下文，agent-as-tool=调用黑盒隔离 token。
平台现只有 Handoff；C 给的是调用式隔离。）

**现状**：`RuleToolExecutor`/`RuleRegistry`/`RuleTool` 已实现 + 7/7 测试绿（含模拟 LLM agent 调 `run_rule`
端到端：业务工具 Echo 对外层 agent 隐藏、executor 内部调一次、压缩结果回填）。**未做**：Hosting DI 注册
（`AddDesktopCommon` 加一行把 `run_rule` 注册进 `ToolRegistry`，并把 `toolResolver` 接到 `ToolRegistry`）+
专家编辑器把规则程序注册进 `RuleRegistry`。属 1.1 收尾。
