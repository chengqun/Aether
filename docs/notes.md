# Notes

Notes 是 Aether 的笔记与知识持久化层：一棵 SQLite 树承载 4 种节点（笔记 / Agent / Workflow / 知识对象），统一存储、树形组织，AI 通过工具读写。位于 `business/Notes/` 限定上下文。

## 节点模型

4 种节点共用 `note_node` 表，差异只在 `node_type` + `content` 编码：

| node_type | 类型 | Content 格式 | 解析器 | 职责 |
|---|---|---|---|---|
| 0 | Note | 纯 Markdown / 代码（`syntax` 标记 markdown/sql/python/json…） | 无（直接存原文） | 人类阅读、业务背景、代码片段 |
| 1 | Agent | YAML front-matter + Markdown 系统提示词 | `AgentSourceParser` | 自定义 Agent：工具/采样参数/能力开关 + system prompt |
| 2 | Workflow | JSON（嵌套 handoff 结构） | `WorkflowSourceParser` | 多 Agent Handoff 编排：参与者 + 转交边 + 指令 |
| 3 | Knowledge | YAML front-matter（kind+links）+ Markdown body | `KnowledgeSourceParser` | 知识图谱节点：流程 / 数据源 / 规则 |

每条节点字段：`id` / `parent_id` / `sort_order` / `title` / `content` / `syntax` / `tags` / `icon` / `node_type` / `linked_id` / `source` / `marketplace_id` / `display_name` / `created_at` / `updated_at`。单用户（无 user_id）、树形（ParentId + SortOrder）。

- **加载**：`INoteService.GetAllAsync()` 拉全表按 sort_order 排，客户端用 ParentId 在内存组树。
- **变更通知**：`NoteService` 在 Create/Update/Delete 后触发 `NotesChanged` 事件，`NotesViewModel` 据此刷新树（保留展开/选中态），并联动 `Experts`/`Workflows` 列表。
- **删除**：删节点时子节点 `parent_id` 置 NULL（提升为根），不级联删除。

## 节点关系

```
Workflow --calls--> Agent --uses--> Knowledge --typed link--> 子笔记 / 资产
Agent --reads/writes--> Note
```

- **Agent** —— 调工具、生成结果
- **Workflow** —— 多 Agent 转交编排
- **Knowledge** —— 结构化领域知识（可复用资产）
- **Note** —— 自由文本（人读为主）

## 知识对象（Knowledge）

Knowledge 节点是一个最简知识图谱模型，用 typed links 把流程/数据源/规则组成可复用的资产网络。

### 模型

```
KnowledgeObjectDefinition
    ├── Kind    分类（自由字符串：流程 / 数据源 / 规则 / …）
    ├── Links   typed 引用列表（图边）
    └── Body    markdown 正文（说明 / 字段表 / SQL 模板 / 任何 markdown）
```

`KnowledgeLink { Role, TargetId?, Target }`：
- `Role` 自由字符串（uses / then / applies-to / produces / depends-on…）
- `TargetId` 权威引用；`Target`（标题）回退（老数据无 id）
- `name` = 笔记标题，不在 content 里

持久化为 YAML front-matter（`kind` + `links`）+ markdown body，与 Agent 节点同构（`KnowledgeSourceParser`，YamlDotNet，容忍无 front-matter 的老笔记）。

### typed links 与 DAG 纪律

- 边**单向**，只留「流程 uses/then → 资产」这类正向依赖边。
- **不要写反向回指边**（`consumed-by` / `schema-for` / `consumes`）——冗余，且会被 `FormatAsync` 当噪音内联进 read 产物。
- 分层：流程层（`kind=流程`）带 `uses`/`then` 出边是消费方；资产层（`kind=数据源`/`规则`，`links: []`）是被引用的复用目标。

### AI 读取

`KnowledgeTool`（`ai/Notes/Acme.Notes.AI/Tools/KnowledgeTool.cs`），只读：

- `ListKnowledgeObjects()` —— 列所有 Knowledge 节点（kind + body 80 字预览）。
- `ReadKnowledgeObject(name)` —— `FormatAsync` 拼装成 `kind:` / `name:` / body + `links:` 段，每个 link 输出 `[role] target:` + 目标笔记**完整原始 Content** 逐行缩进 2 格。**只展开一层**，子节点自身的 link 不递归。
- 读是非破坏的、per-flow 的：不改笔记，每次 read 现场拼装。按需 `read` 才进上下文。

复用语义：资产节点改一处，所有引用它的流程下次 `read` 都拿新版。与 Agent 混用时，硬契约（输出格式、表范围约束）留 agent body，重的参照数据（字段表、规则库、SQL 模板）抽成知识对象按需 `read`。

### 图查看

知识对象编辑器有「查看图」按钮：`KnowledgeEditView` → `BuildGraphModel()` → `WorkflowGraphBuilder.Layout()` → `WorkflowGraphWindow` 弹窗，用 Avalonia Canvas 原生绘制节点 + typed link 边 + role 标注 + 高亮当前对象。另附「复制 Mermaid」按钮导出 mermaid 文本。仅查看，不可在图上编辑。

### 落地状态

代码已实现：`KnowledgeSourceParser`（解析）+ `KnowledgeTool`（`ListKnowledgeObjects` / `ReadKnowledgeObject` / `CreateKnowledgeObject` / `UpdateKnowledgeObject`）。AI 现在能在对话里创建、读取、更新知识对象，无需手拼 YAML。notes.db 当前无 KO 数据——可通过对话让 AI 创建，或手写/直写库导入 `~/Desktop/knowledge-objects/*.yaml`。

### 迭代闭环（与编程类比）

知识对象的创建-读取-执行-调整构成一个闭环，结构与 edit-compile-run 同构：

```
对话描述业务
    ↓  CreateKnowledgeObject(name, kind, body, links, parentId)
写入知识对象（笔记树）
    ↓  ReadKnowledgeObject(name)
读取 + 按 typed links 内联展开子笔记
    ↓  agent 按知识对象推理执行
执行结果
    ↓  不符合预期？
    ├─ 是 → UpdateKnowledgeObject(name, kind?, body?, links?) → 回到读取
    └─ 否 → 完成
```

与编程的类比：

| 编程 | 知识对象 |
|---|---|
| 源代码文件 | 知识对象节点（front-matter + body）|
| 编写代码 | `CreateKnowledgeObject` |
| 加载/编译源码 | `ReadKnowledgeObject`（内联 links 进上下文）|
| 运行程序（解释器调度库调用）| agent 按知识对象调度 tools/skills 执行 |
| 修代码重新跑 | `UpdateKnowledgeObject` |
| 代码库 | notes.db 的知识对象集合 |
| 模块依赖（import）| typed links（uses/then/applies-to）|

运行机制：知识对象（规格）经 `ReadKnowledgeObject` 加载进上下文后，agent 按其**编排调度 tools 和 skills** 完成执行——`tools`（SQL 查询/shell/文件/云文档等）和 `skills`（prompt + Python 脚本）做实际动作，返回真实数据与结果。确定性在 tool/skill 执行本身（SQL 查什么得什么、脚本跑什么得什么），不确定性在 LLM 的调度决策（按规格选哪个 tool、填什么参数）。所以不是"纯提示词空转"——是**自然语言规格驱动的 tool 编排**，类比解释型语言：知识对象=程序规格+调度逻辑，tools/skills=标准库/系统调用，LLM=解释器。

当前闭环里"验证"环节靠人看执行结果判断；要全自动闭环，还需 agent 执行后自动校验结果、反馈调整知识对象。

## 运行时

### 分层

```
AI Agent
   ↓
HarnessRuntime（Aether.AI.Harness，包装 Microsoft Agent Framework）
   ↓
AgentFactory（定义 → 运行时 Agent）+ ToolRegistry（工具池）
   ↓
ai/<Context>/Acme.<Context>.AI 适配器（Capability Layer）
   ↓
business/<Context> Application 契约 → Infrastructure → 外部系统
```

- **HarnessRuntime**：流式运行循环（`RunStreamingCoreAsync`）、会话/记忆、工具调度、审批。
- **AgentFactory**：把 `CustomAgentDefinition`（来自 Agent 节点）解析为 MAF `AIAgent`，装配工具/采样/能力开关/记忆策略。
- **ICustomAgentRunner / ICustomWorkflowRunner**：Agent / Workflow 运行入口（CRUD + 流式运行）。
- **ai/ 适配器**：每个业务上下文一个，注册 `AIFunction` 工具，调 Application 契约（不直接碰 Infrastructure/Domain）。

### 与 Microsoft Agent Framework 集成

```
User → Microsoft Agent Framework → HarnessRuntime / AgentFactory
                                   ├→ KnowledgeTool + NoteTool（Notes Repository）
                                   └→ Business Tools（ai 适配器）
        ↑                              ↓
        └────────── Agent（推理决定下一步）←┘
```

- **单 Agent**：`AgentFactory.Create` → MAF `AIAgent`，经 `HarnessRuntime.RunStreamingAsync`。
- **Workflow**：`WorkflowFactory.BuildAsAIAgent` 把 MAF Handoff 工作流包成 `AIAgent`，复用同一条 `RunStreamingCoreAsync` 流式管线。

### 工具链

| 上下文 | 适配器 | 工具 |
|---|---|---|
| 笔记仓库 | `Acme.Notes.AI` | `NoteTool`（List/Read/Search/Create/Update/Append/Delete）、`KnowledgeTool`（List/Read/Create/UpdateKnowledgeObject） |
| 毓数平台 | `Acme.Qifu.AI` | `YushuSqlQueryTool`（异步 SQL + 轮询/下载）、`CloudDocsUploadTool`、`TeamsMessageTool` |
| 系统工具 | `Acme.SystemTools.AI` | `FileRead/Write/Edit/LineEdit/Delete`、`Glob`、`Grep`、`FileDiff`、`DirectoryExplorer`、`CommandExecutor`、`ShellCommand`、`WebFetch` |
| CTP 期货 | `Acme.Ctp.AI` | `CtpLogin`、`OrderAction/Insert`、`QueryAccount/MarketData/Position` |
| 行情 | `Acme.Quotes.AI` | `SecurityQuery`、`TechnicalAnalysis` |
| 交易 | `Acme.Trading.AI` | `Backtest` |
| APK 解析 | `Acme.ApkParser.AI` | `ApkInfo` 系列 |
| 任务管理 | (内置) | `todos_add/complete/get_*`、`mode_get/set`、`ask_user` |

### AI 消费流程

1. **读笔记**：`ListNotes / ReadNote / SearchNotes`
2. **知识对象 CRUD**：`ListKnowledgeObjects` / `ReadKnowledgeObject`（按 typed links 内联展开一层）/ `CreateKnowledgeObject`（可指定父节点）/ `UpdateKnowledgeObject`（迭代调整 kind/body/links）
3. **执行**：`YushuSqlQueryTool` 查数据、`ShellCommandTool` 跑脚本、`CloudDocsUploadTool` 产文档、`TeamsMessageTool` 通知
4. **沉淀**：结论写回笔记

## 数据存储

- **位置**：每租户独立，`~/Library/Application Support/Aether/Desktop-<Tenant>/notes.db`（DI 默认 `~/.aether/notes.db`，可由 `NOTES_DB_PATH` 覆盖）。
- **表**：`note_node`（见节点模型字段）。Guid 存 TEXT，时间戳存 ISO-8601 TEXT，`node_type` 存 `(int)NoteNodeType`。WAL + busy_timeout=5000。
- **迁移**：`EnsureAsync` 建表 + `ALTER TABLE ADD COLUMN` 幂等吞异常（目前仅 `icon` 列）。
- **挂载结构**：根节点下挂「智能体」（含 `agent` / `workflow` / `知识对象` 三个 folder）、「风控笔记」等业务分类。
