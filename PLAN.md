# labs 文档版成为权威入口（桌面端一期）实现计划

## 目标（用户已确认）

1. **labs 页**：默认打开内置 labs 目录，可改目录。
2. **Agent/Workflow 创建/保存落到 labs 文件**；改目录后定义随之切换（labs 目录 = 权威存储）。
3. **页面所有从 note 节点加载的数据全切 labs 文件**（Stockapp 策略/规则/Lean、桌面专家/规则/工作流等）。
4. **删 note 版 AI 工具**（`NoteTool`/`KnowledgeTool` 的 note 读取）；note 树只保留自身侧边栏纯文本功能。

**关键决策（已确认）**：
- note 树**完整保留**（9 种节点编辑/展示不动，作为第二套可用的遗留入口）。
- **不迁移**现有 note form 节点数据到 labs；labs 从空目录开始。

---

## 核心难点：agent 身份从 `Guid`（note 节点 id）切换到 `string`（labs 相对路径）

现状：agent/workflow 的运行时身份是 **`Guid`**，贯穿：

| 位置 | 类型 | 现状 |
|---|---|---|
| `ICustomAgentRunner.RunStreamingAsync` | `Guid agentId` | Core |
| `ICustomAgentCatalog.GetAsync/SaveAsync/DeleteAsync` | `Guid id` | Notes.Domain |
| `CustomAgentCatalog`/`CustomAgentRunner` | `Guid` 键 | ai/Agents |
| `ExpertItem.Id` | `Guid` | Mvvm |
| `ExpertsViewModel`/`RulesViewModel`/`WorkflowsViewModel` | `Guid` | Mvvm |
| `ConversationBackedAgentExecutor`（Automation） | `Guid` agentId | Mvvm |
| `AgentManagementTool`（agent 自举） | `Guid` | Shared |
| `Conversation.AgentId` → `AgentRun.AgentId` | **已是 `string`** | Core ✓ |

好消息：`AgentRun.AgentId`/`AgentRunContext` 已是 `string`（`Guid.ToString()`）。所以**持久化层已用 string**，只有「运行时运行器 + UI 模型」这一圈是 `Guid`。

**labs 文件的稳定身份 = 相对路径（string）**。两个可选方案：

### 方案 1（推荐）：agent 身份保持 `Guid`，labs 文件用「稳定 Guid 存于文件 front-matter」

- labs 的 agent/workflow 文件 front-matter/JSON 里带一个 `id: <guid>` 字段。
- 首次创建时生成 `Guid` 写入文件；之后 edits 按该 `Guid` 定位（重命名文件不断链，因为 id 在内容里，不在路径里）。
- 现有 `Guid` 型 `ICustomAgentRunner`/`ICustomAgentCatalog`/`ExpertItem.Id` **不需要改签名**——只是 `CustomAgentCatalog` 的读/写实现从「note 节点」换成「labs 文件（id 存文件内）」。
- **好处**：运行时运行器、Automation、会话持久化、AgentManagement 全部零改动；只有 catalog 的存储后端换。
- **代价**：labs 文件里多一个 `id` 字段（对用户可见但无害，agent 文件本来就有 front-matter 元数据）。

### 方案 2：agent 身份切 `string` 路径

- `ICustomAgentRunner`/`ICustomAgentCatalog`/`ExpertItem.Id` 全改 `string`。
- **代价**：改动面巨大（Core 契约 + 运行器 + 4 个 VM + Automation + 自举工具 + 会话持久化映射），且 `Conversation`/`AgentRun` 里存的 `Guid` 历史数据要兼容迁移。

**→ 采用方案 1**：改动集中在 catalog 后端（note→labs 文件），身份模型 `Guid` 不变，运行时/UI/持久化零松动。这是「把 note 节点里的 id 语义，映射到 labs 文件内的 id 字段」的邻接替换，风险最低、可回滚。

---

## 分层归属

- **labs 目录根**：沿用 `IKnowledgeDirectoryProvider`（apps/Mvvm 已有），默认根改 `<tenantDir>/labs`。
- **labs 版 catalog**：新建 `DirectoryAgentCatalog` / `DirectoryWorkflowCatalog`（ai/Agents 或宿主），实现已有 `ICustomAgentCatalog`/`ICustomWorkflowCatalog` 契约，但读写 labs 文件（`DirectoryNoteCatalogSource` 扫文件 + 各 `Directory*Parser` 解析 + 文件内 `id` 字段做 Guid 身份）。
- **文件→定义解析**：已有 `DirectoryNoteCatalogSource`（枚举）+ `DocumentKnowledgeSourceParser`/`DocumentWorkflowSourceParser`（文档版）；需补 Agent/Rule/CodeRule 的文档版「id 字段」处理（见下）。
- **note 版 catalog 保留**：`CustomAgentCatalog`（note 版）不删，但桌面端 DI 默认注册换成目录版；note 树自身功能（NotesViewModel/NoteNodeEditVmFactory）继续走 `INoteService`，不经过 catalog。

---

## 文件格式（labs 内）

```
<labsRoot>/
  agents/*.agent.md         # Agent：front-matter(含 id:guid) + markdown body
  workflows/*.workflow.json # Workflow：JSON(含 id:guid + entryAgent/participants 相对路径)
  rules/*.rule.yaml         # Rule：单 YAML(含 id:guid)
  coderules/*.coderule.cs   # CodeRule：front-matter(含 id:guid) + C#
  knowledge/*.md|*.ok       # Knowledge：已有（DocumentKnowledgeSourceParser）
  strategies/*.strategy.yaml
  codestrategies/*.cs
  leanalgorithms/*.lean.cs
```

关键：Agent/Rule/CodeRule/Workflow 文件都要带 **`id`（稳定 Guid）** 字段，作为运行时身份（方案 1）。Knowledge 不带 id（无运行时身份，Guid 只在 note 版才有意义，文档版用路径）。

---

## 一期实施步骤

### 步骤 1：labs 目录根默认值 + 目录 provider（先让 labs 页能开默认目录）

- `IKnowledgeDirectoryProvider` 默认：无已存根时 `<tenantDir>/labs`（`KnowledgeDirectoryProvider` 或宿主启动处设默认）。用户可改目录（已有「打开目录」「关闭目录」能力）。

### 步骤 2：目录版 catalog（Agent/Workflow 读写 labs 文件）

- 新建 `DirectoryAgentCatalog : ICustomAgentCatalog` + `DirectoryWorkflowCatalog : ICustomWorkflowCatalog`（放 ai/Agents，或宿主；读用 `DirectoryNoteCatalogSource`，写直接落盘文件）。
- `id` 字段：`DirectoryAgentCatalog.SaveAsync` 首次建文件时生成 `Guid` 写进 front-matter 的 `id:`；Update 按 id 定位文件重写。
- 需要文档版 Agent/Rule/CodeRule 解析（带 `id` 字段）——在 `AgentSourceParser`/`RuleSourceParser`/`CodeSourceParser` 的 front-matter 里加 `id:` 键（它们本来就有 `Serialize` 输出，加一字段即可），或新建文档版 parser。**决策**：复用 note 版 parser 加 `id:` 字段（Agent/Rule/CodeRule 没有 Guid 跨引用，唯一差异是 `id` 字段），比另建文档版 parser 更省。
- 桌面端 DI：`ICustomAgentCatalog` → `DirectoryAgentCatalog`、`ICustomWorkflowCatalog` → `DirectoryWorkflowCatalog`（替换 `AddCustomAgents`/`AddCustomWorkflows` 里的 note 版注册，或通过配置开关）。

### 步骤 3：页面数据源切换（3.a 策略/规则/Lean 已接抽象，3.b 专家/规则/工作流页切目录 catalog）

- 3.a Stockapp `StrategyNoteSource`/`LeanAlgorithmNoteSource` + 桌面 `CodeNoteSource`：**已接 `INoteCatalogSource`**（上一轮），只需宿主把 `INoteCatalogSource` 换注册为 `DirectoryNoteCatalogSource`（绑 labs 根）。
- 3.b 专家页 `ExpertsViewModel`、规则 `RulesViewModel`、工作流 `WorkflowsViewModel`：它们注 `ICustomAgentCatalog`/`ICustomWorkflowCatalog`——步骤 2 换注册后**自动**从 labs 读写，VM 零改动（仍用 `Guid Id`，只是后端换成目录）。删除/编辑/召唤的 `Guid` 语义不变。
- 3.c 专家栏「市场」tab 删除（用户要求）。

### 步骤 4：删 note 版 AI 工具

- `AddNotesTools()` 里删 note 读取工具：`NoteTool` 的 `ReadNote/ListNotes/SearchNotes/ListKnowledgeObjects/ReadKnowledgeObject`（读路径），保留或删「创建类」按需。**决策点**：`NoteTool` 的写工具（CreateNote/AppendNote/UpdateNote/DeleteNote）与「note 侧边栏自身功能」的关系——用户说「note 节点仅留侧边栏功能」，即 AI 不再操作 note。故 **`AddNotesTools()` 整体移除**（或在 `DesktopCompositionExtensions` 不再调用它），保留 `DocumentKnowledgeTool`（labs 知识对象 AI 工具，已是文件版）。
- 注意：`ai/Notes/Acme.Notes.AI` 适配器本身可保留（代码不删），只是桌面端不再 `AddNotesTools()`。

### 步骤 5：清理 + 文档 + 记忆

- CLAUDE.md 更新知识对象/labs 段；记忆落档。

---

## 明确不做（一期红线）

- ❌ 不删 note 树的 8 种 form 节点编辑功能（`NotesViewModel`/`NoteNodeEditVmFactory`/`NoteNodeTypeRegistry` 全部保留）。
- ❌ 不改 `ICustomAgentRunner`/`ICustomAgentCatalog` 的 `Guid` 签名（方案 1）。
- ❌ 不迁移旧 note form 数据到 labs。
- ❌ 不改手机端（MAUI）——此期只桌面端。

---

## 待确认关键点（实现前）

1.（方案选择）agent 身份：**方案 1（文件内 `id: guid`，保持 `Guid` 签名）** 默认采纳；若你要「路径即身份」请明说。
2.（AI 工具）`AddNotesTools` 里 note 工具的删除范围：**整体移除**（AI 不再碰 note），还是只删「读」工具留「写」？我倾向整体移除（对齐「note 只留侧边栏」）。
3.（catalog 放哪）目录版 catalog 放 `ai/Agents`（与 note 版并列）还是宿主层？我倾向 `ai/Agents`（同一契约 `ICustomAgentCatalog`，目录版实现放适配器，镜像 note 版 `CustomAgentCatalog`）。

确认这 3 点后我出最终执行计划并按序开工。

---

## 实施进度（已落地）

**已确认决策**：① 方案 1（文件内 `id: guid`，`Guid` 签名不变）→ 工作流天然 Guid（`CustomWorkflowDefinition` 已是 `Guid`，`WorkflowSourceParser.Serialize` 直接序列化整个对象 → JSON 天然含 `id` 字段）。② `AddNotesTools` 整体移除（AI 不再碰 note）。③ 目录版 catalog 放 `ai/Agents`。

- [x] **步骤 1**（labs 目录默认根）：`IKnowledgeDirectoryProvider` 加 `DefaultRoot`（`<tenantDir>/labs`）；`KnowledgeObjectsViewModel.LoadLastRootWithDefaultAsync` 无持久化目录时回退打开默认根（不存在则创建）。
- [x] **步骤 2**（目录版 catalog）：`DirectoryAgentCatalog` / `DirectoryWorkflowCatalog`（`ai/Agents`）实现 `ICustomAgentCatalog`/`ICustomWorkflowCatalog`，读写 labs 文件；`id:` 字段 = 运行时 `Guid` 身份（存于 front-matter / JSON）。Agent/Rule/CodeRule 三个 parser 各加 `id:` 键解析+序列化。
- [x] **步骤 3.a**（策略/规则/Lean 源切目录）：`CodeNoteSource`/`StrategyNoteSource`/`LeanAlgorithmNoteSource` 已接 `INoteCatalogSource` 抽象；宿主 DI 把 `INoteCatalogSource` 换注册为 `DirectoryNoteCatalogSource`（绑 labs 根）。
- [x] **步骤 3.b**（专家/规则/工作流页切目录 catalog）：DI 里 `ICustomAgentCatalog`→`DirectoryAgentCatalog`、`ICustomWorkflowCatalog`→`DirectoryWorkflowCatalog`（后注册覆盖 note 版，末位生效）。VM 零改动（仍 `Guid Id`）。
- [x] **步骤 3.c**（删市场 tab）：`ExpertsView.axaml` 移除「市场」tab + Market 面板。
- [x] **步骤 4**（删 note 版 AI 工具）：`DesktopCompositionExtensions` 不再 `AddNotesTools()`，改 `AddSystemTools().AddApkParserTools()` + `AddMemoryTools()`（长期记忆工具）等。note 版适配器代码保留但桌面端不调用。
- [x] **清理**：`IDocumentWorkflowSourceParser`/`DocumentWorkflowSourceParser`（路径字符串版）死代码整删（接口+实现+测试+DI 注册）——工作流彻底走 Guid 方案 1，与最终决策一致。
- [x] **labs 页改名**（侧边栏 nav「知识对象/知识·SCM」→「labs/labs·SCM」+ 树头「labs」；`PanelKeys`/icon key 未动避布局回退）。
- [x] **`.ok` 后缀全仓清除**（用户「.ok 是过去式」）：knowledge 文件统一 `.md`，`.ok` legacy 回退整套删除（~130 处 / 34 文件）。老项目 `.ok` 将无法打开（已知悉）。

**note 版 catalog 保留**（`CustomAgentCatalog`/`CustomWorkflowCatalog` 读走抽象、写仍走 SQLite），作为可回滚出口；桌面端默认切目录版。

**构建/测试**：全解决方案 `Aether.AI.Platform.slnx` 0 错误；`Acme.Agents.AI.Tests` 29 绿；`Acme.Notes.Infrastructure.Tests` 6 绿。

### 剩余（二期/待办）

- **步骤 5**：CLAUDE.md 知识对象/labs 段待补（知识对象 nav 页那节已描述 labs 根 + `IKnowledgeDirectoryProvider`，但「labs 成权威入口」的 agent/workflow 文件版 catalog 尚未入文档）。
- **运行时运行器零改动（已验证）**：`CustomAgentRunner.RunStreamingAsync` 经 `_catalog.GetAsync(agentId)`（`ICustomAgentCatalog`）、`CustomWorkflowRunner` 经 `_workflowCatalog.GetAsync` + `_agentCatalog.GetAsync(pid)` 取定义——DI 已把两者分别覆盖为 `DirectoryAgentCatalog`/`DirectoryWorkflowCatalog`，故「建 agent/workflow 文件 → 召唤到聊天 → 跑通」自动从 labs 目录读，运行器代码零改动。端到端「建 agent → 召唤 → 跑通」仍建议人工跑一次验证（文件刚落地新目录）。
- **note 树 form 节点编辑功能**保留（红线），未动。
- **手机端（MAUI）**未动（红线）。

---

## 三期：labs 页升级为 9 类型统一编辑器

用户需求：labs 页不再只是「知识对象」，而是 labs 目录下 **9 种节点类型的统一编辑入口**——树按目录分组展示 9 类文件，点开按目录反查类型 → 分流到各自编辑器。

**方案**（已确认）：保留 labs 根 + 9 类型子目录（`CatalogNoteDirectory` 已是权威映射，目录名即类型）；树按目录分组（树第一层=9 个子目录）；打开按 `CatalogNoteDirectory.TypeForDirectory(目录名)` 反查 `NoteNodeType` 分流。

**类型 → 编辑器分派**（镜像 note 页）：
- **form 类型（6 个）**：Agent（`ExpertEditView`）、Rule（`RuleEditView`）、CodeRule（`RuleEditView`）、Workflow（`WorkflowEditView`）、Strategy（`StrategyEditView`）、Knowledge（`KnowledgeEditView`）→ 上栏表单 + 下栏源码两栏。
- **代码类型（3 个）**：CodeStrategy、LeanAlgorithm、Note → 全宽代码编辑器（`SyntaxMap` 按扩展名高亮）。

**关键复用**：
- `ExpertsViewModel.CreateEditViewModel` / `RulesViewModel.CreateEditViewModel`+`CreateCodeRuleEditViewModel` / `WorkflowsViewModel.CreateEditViewModelAsync` —— 建 edit VM，已走目录版 catalog。
- edit VM 类型（`ExpertEditViewModel` 等）+ 对应 view（`ExpertEditView` 等）零改。
- note 页 `NotesEditorView` 的「上栏按类型切换 Border + IsVisible」XAML 模式。

**不直接复用 `INoteNodeEditVmFactory`**：它深度耦合 `NoteNodeVM.Id`（查 Experts 列表）+ `NotesViewModel`（`TryParseStrategy`/`SaveStrategyAsync`/`GetLinkTargets`/`NavigateToNoteCommand` 等 note 节点专属）。labs 版需自己的派发表：读文件 → parser 解析 `id:`/Guid → 在 Experts/Rules/Workflows 列表定位 item → 建 edit VM → 接保存（直接走 `XxxViewModel.SaveAsync`，已目录版落盘）。

**改动用**：
1. `CatalogNoteDirectory` 已就绪（无需改）。
2. `FileNode` 或 labs 树节点携带 `NoteNodeType`（打开分流时由路径→目录名反查，或在 `BuildNode` 时记录）。
3. `KnowledgeObjectsViewModel`：
   - `BuildNode`：枚举 9 类文件（`CatalogNoteDirectory` 驱动），按目录分组。
   - `OnSelectedNodeChanged` + 打开分流：form 类型 → 读文件 → 解析 → 定位 item → 建 `ActiveEditVm`；代码类型 → 全宽编辑器。
   - 加 `IsAgentNode`/`IsRuleNode`/`IsCodeRuleNode`/`IsWorkflowNode`/`IsKnowledgeNode`/`IsStrategyNode` 布尔 + `ActiveEditVm`（镜像 `NotesViewModel`）。
4. `KnowledgeObjectsView.axaml`：上栏按类型切 `ExpertEditView`/`RuleEditView`/`WorkflowEditView`/`KnowledgeEditView`/`StrategyEditView`（镜像 `NotesEditorView`），代码类型全宽。
5. `KoTreeView.axaml`：树图标按类型（`NoteNodeTypeRegistry` 图标）。

**注**：知识对象（Knowledge）编辑器从 `DocumentKnowledgeViewModel`（文档版，路径 link）统一切到 note 版 `KnowledgeEditViewModel`？——文档版 link 是路径 `targetId`，note 版是 `Guid?`。labs 目录用哪种？见 [知识对象文件格式](#) —— labs 的 knowledge 文件 `targetId` 是相对路径（`DocumentKnowledgeSourceParser`），故**保留文档版 `DocumentKnowledgeViewModel`**（Knowledge 类型仍走 `DocumentKnowledgeView`，不切 note 版）。

### 三期实现进度（已完成）

- [x] **树按目录分组**：`KnowledgeObjectsViewModel.BuildNode` 重写——根节点只含 9 个类型子目录（`CatalogNoteDirectory.AllTypes`，存在的才显示）；类型目录收该类型扩展名的文件（`CatalogNoteDirectory.For(type).Extension`）+ 递归子目录（子目录继承类型）；文件节点图标按类型（`NoteNodeTypeRegistry` 的 path+色，描边 Lucide，经 `FileNode.iconOverride`）。显示名去复合扩展名（`foo.agent.md`→`foo`）。
- [x] **类型反查**：`ResolveType`/`ResolveDirType` 由文件/目录路径相对 labs 根首段目录名 → `CatalogNoteDirectory.TypeForDirectory`（单一权威）。未给 `FileNode` 加字段（零侵入，路径反查即可）。
- [x] **打开分流**：`OnSelectedNodeChanged` 切文件先清旧 `ActiveEditVm`（防新类型 Border 显示旧表单），同路径跳过（watcher 恢复选中不重开）。`OpenFileAsync` → `BuildEditVmAsync(type)` 分流。
- [x] **form 类型 6 个照实现**：Agent/Rule/CodeRule/Workflow/Strategy/Knowledge 各 `BuildXxxVm`，读文件→parser 解析 `id:`/Guid（或 Strategy codec 解析）→ 在 `IShellHost.Experts/Rules/Workflows` 列表定位 item → `CreateEditViewModel` → 接 `SaveRequested`/`AutoSaveRequested`（走 `XxxViewModel.SaveAsync`，已目录版落盘）+ `RefreshSourceFromFileAsync` 回刷下栏。
- [x] **代码类型 3 个**（Note/CodeStrategy/LeanAlgorithm）：`ActiveEditVm=null` → 全宽源码编辑器（无上栏）。
- [x] **类型布尔 + 布局**：`IsAgentNode`/`IsRuleNode`/`IsCodeRuleNode`/`IsWorkflowNode`/`IsKnowledgeNode`/`IsStrategyNode`/`IsCodeOnlyNode`/`IsFormNode`/`HasRightPane`/`SplitterVisible`/`RightPaneVisible`/`EditorVisible`（镜像 `NotesViewModel`）。
- [x] **上栏按类型切换**：`KnowledgeObjectsView.axaml` 上栏 `<Grid IsVisible={RightPaneVisible}>` 内 6 个 `<Border IsVisible={IsXxxNode}>` 包 `ExpertEditView`/`RuleEditView`（Rule+CodeRule）/`WorkflowEditView`/`StrategyEditView`/`DocumentKnowledgeView`（Knowledge）。状态条名称改绑 `SelectedNode.Name`（edit VM 无统一 Name）。
- [x] **语法高亮按类型**：`KnowledgeObjectsView.axaml.cs` 的 `OkSyntax`（固定 markdown）改为 `CurrentSyntax()` = `SyntaxMap.SyntaxForExtension(Path.GetExtension(SelectedNode.Path))`（`.rule.yaml`→yaml / `.coderule.cs`→csharp / `.workflow.json`→json / `.agent.md`→markdown）。
- [x] **构造注入 `IShellHost`**：`KnowledgeObjectsViewModel` 构造加 `IShellHost shell`（`MainViewModelBase` 传 `this`），惰性访问 `Experts/Rules/Workflows` 同级 VM（打开文件时才调，此时已构造完成）。

**构建/测试**：全解决方案 0 错误；`Agents.AI.Tests` 29 绿 + `Notes.Infrastructure.Tests` 6 绿。

**未做（后续）**：
- Knowledge 文件格式确认：labs knowledge 用 `.md`（`DocumentKnowledgeSourceParser` 路径 link），保留文档版编辑器（已确认，非 note 版）。

### 三期·按类型新建（已完成）

用户需求：labs 树右键能按类型新建（规则/代码规则/9 类），落到对应类型目录。

- [x] **`KnowledgeObjectsViewModel.CreateNodeAsync(NoteNodeType, name)`**：落盘到 `CatalogNoteDirectory.For(type)` 目录 + 扩展名。form 类型（Agent/Rule/CodeRule/Workflow）经 **parser.Serialize（生成含 `id` 的完整格式）**——不能用 note 树 `DefaultContent`（缺 `id`，`DirectoryAgentCatalog` 按 id 定位会失败）；代码/知识类型（Knowledge/Strategy/CodeStrategy/LeanAlgorithm/Note）直接写 `NoteNodeTypeRegistry.DefaultContent` 模板。默认程序（Rule/CodeRule）从注册表模板提取（Serialize 内 `Deserialize<RuleProgramDoc>` 自动提取 steps，忽略元数据）。
- [x] **`KoTreeView.axaml` 右键菜单**：「新建知识对象」→「新建」子菜单（9 类：Agent/Workflow/规则/代码规则/知识对象/策略/代码策略/Lean 算法/文本笔记），`Tag` 传类型字符串。
- [x] **`KoTreeView.axaml.cs` `OnNewNodeClick`**：解析 `Tag`→`NoteNodeType` → 弹名字框 → `CreateNodeAsync` → `OpenKnowledgeByPathAsync` 打开新文件。

**新建后「看不到源码」bug 修复（2026-08-26）**：三个策略族（CodeStrategy/Strategy/LeanAlgorithm）新建后源码编辑器空白/隐藏。根因是「新建后直接打开」路径（`OnNewNodeClick` → `OpenKnowledgeByPathAsync`）**不设 `SelectedNode`**，而 `ActiveNodeType` 与 `EditorVisible` 都错误地从 `SelectedNode` 派生——`EditorVisible => SelectedNode is not null` = false → XAML 里 `CodeEditor.IsVisible` 绑定 `EditorVisible` 整体隐藏；`ActiveNodeType => ResolveType(SelectedNode)` 反查的是旧选中项而非刚打开的文件 → 布局派生属性全错。修复：① `KnowledgeObjectsViewModel` 加 `_activeType` 状态（`OpenFileAsync` 设、`CloseActiveFile` 清），`ActiveNodeType`/`EditorVisible`/`IsCodeOnlyNode` 等改由 `_activePath`/`_activeType` 派生（不再依赖 `SelectedNode`）；② `OpenFileAsync` 末尾主动发 `KnowledgeSourceUpdated` + `RaiseLayout`（code 类型 `ActiveEditVm=null` 无 `OnActiveEditVmChanged` 触发 View 刷新）；③ `KnowledgeObjectsView.SyncEditor` 不再用 `HasActiveEdit` 门控 `CodeEditor.IsVisible`（旧语义「仅 form 有源码」已废，code 类型也须显示源码）。

---

## 四期：移除 SQLite note 版（最终终态 = 全文档版）

用户最终目标：**彻底移除 SQLite note（note 树 + `note_node` 表 + `INoteService`），全走 labs 文档版**。前提=功能都迁成文档版。已确认：删 note 树 UI、分阶段迁、本期不动手机端。

### 现状：SQLite note 仍被依赖的东西（迁移缺口）

读取端（`INoteCatalogSource` → `DirectoryNoteCatalogSource`）大多已切目录，但**写入端 + 变更通知仍绑 `INoteService`（SQLite）**，存在已断链的不一致：

| 依赖方 | 现状 | 状态 |
|---|---|---|
| **note 树 UI**（`NotesViewModel`/`NotesTreeView`/`NotesEditorView`/`NoteNodeEditVmFactory` + 「笔记」nav tab） | 读 SQLite | 待删（最终态） |
| **策略 AI 工具** `CreateStrategyTool`/`CreateYamlStrategyTool`/`CreateLeanAlgorithmTool` | 写 SQLite note 节点 | **已断链**（写 note、回测页读 labs） |
| **CodeRuleHost** | 订阅 `INoteService.NotesChanged` 触发编译 | **已断链**（触发源 note、读端已切目录） |
| **note 版 catalog** `CustomAgentCatalog`/`CustomWorkflowCatalog` 写路径 | 写 SQLite | 可回滚保留，最终删 |
| **note 版 AI 工具** `NoteTool`/`KnowledgeTool` | 代码仍在（桌面已停用 `AddNotesTools`） | 停用，最终删 |
| **存储层** `INoteRepository`/`SqliteNoteRepository`/`INoteService`/`SqliteNoteCatalogSource`/`note_node` 表 | — | 最终删 |

### 分阶段计划

**阶段 1：写入端迁移（消除断链）——本期核心**
- [x] ① 策略 AI 工具切 labs 文件：在 `IStrategyNoteSource`/`ILeanAlgorithmNoteSource`（`Trading.Runtime.Domain` 端口，已只读）加**写方法**（`SaveAsync(name, content)`/按名字在），host 实现（Stockapp `StrategyNoteSource`/`LeanAlgorithmNoteSource`）写到 `<labs>/codestrategies|strategies|leanalgorithms/<name><ext>`。3 个工具注入端口替代 `INoteService`。
- [x] ② `CodeRuleHost` + 策略/Lean 重编译触发器切目录变更源：新增宿主层 `LabsDirectoryWatcher`（监听 labs 根目录 `FileSystemWatcher`，变更/增删/改名 → 防抖 400ms → `DirectoryNoteCatalogSource.NotifyChanged()`）；`CodeRuleHost.Start` + Stockapp `App.axaml.cs` 的两处 `notes.NotesChanged += ...Reload()` 改订阅 `INoteCatalogSource.Changed`。三处重编译触发（代码规则/策略/Lean）全部脱离 `INoteService.NotesChanged`（写端已不写 SQLite，原订阅棱永不再触发）。

**阶段 2：删 note 树 UI**
- 删「笔记」nav tab（`PanelKeys.Notes` + 两租户 `Workspace` 的 `Panels`/`RegionIds`）+ `NotesViewModel`/`NotesTreeView`/`NotesEditorView`/`NoteNodeEditVmFactory`/`LeftTreePanelView` 的 notes 层。
- **保留** `NoteNodeTypeRegistry`（labs 页 editor 用描述符）+ `NoteIcons`（labs 树图标）+ form 编辑 VM（`ExpertEditViewModel` 等，labs 页复用）。

**阶段 3：删 note 版 catalog + AI 工具 + SQLite 存储层**
- 删 `CustomAgentCatalog`/`CustomWorkflowCatalog`（note 版）+ `NoteTool`/`KnowledgeTool`（ai/Notes 写 note 部分）+ `SqliteNoteCatalogSource`。
- 删 `INoteService`/`INoteRepository`/`SqliteNoteRepository` + `note_node` 表。
- **保留** `ai/Notes` 适配器里的**文档版**工具（`DocumentKnowledgeTool`）+ `DocumentKnowledgeSourceParser` 等。

### 待实施时确认的细节
- `AutomationViewModel`/`IGitRepositoryNotifier`/`App.axaml.cs` 等 `INoteService` 引用逐个核实迁移或移除。
- note 树删除后，`NoteNodeTypeRegistry` 里 `IsFormNode/HasAgentRunner/IsChatTarget` 旗标哪些还用于 labs 页（部分可能随 note 树退役）。
