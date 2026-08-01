# 聊天页 UI 重设计方案（向 Claude Code / Trae / Cursor 靠拢）

> 状态：**仅方案，未实施**。本文档供后续实施参考。
> 范围：HarnessAgent 对话流的内容显示与结构。聚焦"内容显示问题"。
> 现状基线：`apps/Desktop/Acme.Desktop.Shared/Shell/ChatView.axaml` + `apps/Acme.Shell/ChatMessageItem.cs` + `MainViewModelBase.RunSegmentedStreamAsync`。

---

## 1. 当前 UI 审计：内容显示问题

| # | 问题 | 现状代码/表现 | 影响 |
|---|---|---|---|
| 1 | 工具结果裸 JSON | `DictToStringConverter` 把 `Input`/`Output` pretty-print 成 `{"result":"北京：晴"}` | 难读、噪音大，看不出工具实际做了什么 |
| 2 | 流式答案是裸 markdown 源码 | 答案流式进 🤔 卡用等宽纯文本，能看到 `## 标题\n- 列表` 原文，结束才渲染 | 流式时丑、结束时跳变 |
| 3 | 推理卡太重 | 💭 卡 `IsStreamingExpanded=true` 默认展开、等宽大段、与答案同对比度 | 占屏、干扰主线 |
| 4 | 无轮次分组 | 用户消息→思考→工具→思考→答案 全扁平散卡 | 一轮里 5 工具+3 思考=8 张孤立卡，看不出归属 |
| 5 | 工具步摘要非"动词+对象" | `ArgSummary` = `city=北京 arguments=[...]` | 不如 `🔎 GetWeather(北京)` 直观 |
| 6 | 计划卡内联 transcript | 📋 卡随流滚动、与工具卡混排 | 计划应常驻可见，不该被滚走 |
| 7 | 无 turn 级状态 | 无"运行中/完成/出错"指示 | 不知道 agent 这轮到哪了 |
| 8 | 长结果撑爆 | 大文件/搜索结果整段 JSON 展开 | 刷屏 |

**核心三件：#1 #2 #3** = 你说的"内容显示问题"。

## 2. 主流 agent UI 模式参考

- **Claude Code**：turn 分组（用户请求作头，assistant 产出缩进）；工具步一行 `verb + target`（Read/Bash/Edit + 文件名/命令），点开看详情；推理默认折叠淡色；编辑类工具出 diff；todo 为 live 更新的 checklist。
- **Trae**：卡片化但紧凑；独立计划面板；工具结果按类型（代码块/表格/终端）；turn 头部状态。
- **Cursor**：工具调用压缩成 pill/单行；diff 预览；推理可折叠。
- 共性：**密度高、工具/推理默认折叠、答案 markdown 实时渲染、结果按类型渲染、turn 分组、计划常驻**。

---

## 3. 数据模型变更建议

### 3.1 `ChatMessageItem`（现有，扩展）
新增字段支撑按类型渲染与分组：
- `int ToolKind` —— 工具语义类别（0 通用 / 1 文件读 / 2 文件写 / 3 命令 / 4 搜索 / 5 笔记 / 6 行情 / 7 计划查询 …），驱动图标与结果渲染器。
- `string? ResultText` —— 从 `Output` 归一出的"主结果文本"（取 `result`/`output`/`content` 等常见 key 的字符串值），供一行展示。
- `bool IsResultLong` / `int ResultLineCount` —— 控制默认折叠+限高。
- `string? ResultKind` —— `text` / `code` / `markdown` / `json` / `table` / `error`，选择渲染模板。
- 现有 `IsReasoningContent`/`ApprovalState`/`Todos` 保留。

### 3.2 新增 `ChatTurnItem`（turn 分组容器，Tier B）
- `ObservableCollection<ChatMessageItem> Children` —— 本轮的思考/工具/答案。
- `string Header` —— 用户请求摘要。
- `TurnStatus Status` —— `Working`/`Done`/`Error`/`AwaitingApproval`。
- `string? Elapsed` —— 耗时。
- `ChatMessageItem? LivePlan` —— 钉在 turn 顶部的计划卡引用（Tier B3）。
- `Messages` 列表改为 `ObservableCollection<ChatTurnItem>`（或 user 消息也作为 turn 的一种）。

> **兼容**：持久化/恢复（`PersistTurnAsync`/`FromTranscriptMessage`）只存工具步+思考+答案，turn 容器是纯 UI 聚合，不持久化（同 plan/approval 卡策略）。

---

## 4. Tier A：内容渲染修复（投入小、收益最大）

### A1. 工具结果按类型渲染（替换裸 JSON）

**目标**：折叠态一行"主结果"；展开态按 `ResultKind` 选模板；长结果限高+"展开更多"。

**归一逻辑**（`HarnessRuntime.ToOutputDict` 之后，VM 侧加 `NormalizeToolResult`）：
1. 从 `Output` 字典取主结果：依次尝试 `result`/`output`/`content`/`text`/`value` 键的字符串值 → `ResultText`。
2. 判 `ResultKind`：
   - 含换行 / 疑似代码（`{`/`def `/`function `/`class ` 等）→ `code`
   - 纯短字符串 → `text`
   - 多键 dict 且非单 result → `table`（key-value 表）
   - 异常/含 "error"/"失败" → `error`（红）
3. `ResultLineCount = ResultText.Count(c=>c=='\n')+1`；`>20` 行 → `IsResultLong=true`，默认折叠到 8 行。

**渲染模板**（`ChatView.axaml` 工具卡展开区，按 `ResultKind` 切 `IsVisible`）：
- `text` —— 单行 `TextBlock`，淡色。
- `code` —— `SelectableMarkdownViewer` 渲染 ```` ```lang\n...\n``` ````（复用现有 markdown 代码块 + 等宽 fallback），`MaxHeight=240` + 滚动。
- `table` —— `ItemsControl` 的 key-value 两列。
- `error` —— 红色 `TextBlock`。
- 折叠态：一行 `ResultText` 截断 + 行数徽标（如 `· 142 行`）。

**各工具结果渲染规约**（按本项目工具适配器）：

| 工具 | 动词图标 | 结果渲染 |
|---|---|---|
| `todos_*` | 📋 | 抑制工具卡，进计划面板（Tier B3） |
| Notes 读笔记 | 📖 | markdown 预览 |
| Notes 增删改 | ✏ | 一行 `已创建/更新/删除 <标题>` |
| CTP 行情查询 | 📈 | 数字表格（合约/最新/涨跌） |
| CTP 下单（审批后） | 🔏 | 一行 `已报单 <合约> <买/卖> <数量>@<价>` |
| Quotes K线/查询 | 📊 | 表格或截断 JSON |
| Trading 回测 | ▶ | 关键统计表 + 净值曲线缩略图（如可行） |
| Qifu 毓数查询 | 🔎 | 表格 / 截断结果 |
| 云文档 | 📄 | markdown 或链接 |
| ApkParser | 📦 | 解析字段表 |
| SystemTools（命令/文件） | ▶ / 📖 | 命令→终端块；文件读→代码块 |
| Skills `run_skill_script` | ▶ | stdout 终端块；非零退出码→error |
| 通用兜底 | 🔧 | 现有 JSON（仅当无法归一时） |

> 动词图标映射放 `ToolVerbIcons`（新文件，按 `ToolName` 前缀/分类映射），与 `NavIcons` 同风格（Lucide path）。

### A2. 流式答案实时 markdown

**现状**：`OnToken` 把答案 token 追加到 `cursor.Current.Content`，UI 用 🤔 等宽卡显示原文，`FinalizeSegment` 才翻 markdown。

**改法**：答案段（`IsReasoningContent=false`）流式时直接绑定到 `SelectableMarkdownViewer.Markdown`（增量刷新）。`Markdown.Avalonia.Tight` 支持运行时改 `Markdown` 属性重渲染；为避免每个 token 全量重渲染的卡顿，节流（Dispatcher 50ms 合并刷新）。完成后无跳变。
- 推理段（`IsReasoningContent=true`）仍用等宽 💭 卡（思考原文就是半结构化文本，markdown 不合适）。
- 流式态视觉：答案段头部小字"回答中…"，去掉 🤔 大图标。

### A3. 推理卡折叠 + 淡色

- 完成态：`IsExpanded=false` 默认，一行 `💭 思考 · 12s ›`，`Foreground=TextSecondaryBrush`、`FontStyle=Italic`。
- 流式态：`💭 思考中…`（带省略号动画），等宽正文限高 `MaxHeight=180`。
- 点头部切换 `IsExpanded`。

---

## 5. Tier B：结构化（投入中等）

### B1. Turn 分组容器
- `Messages` 改为 `ChatTurnItem` 列表；用户消息 + 其后所有 agent 产出收进一个 turn。
- 视觉：左侧 2px 竖线（`AccentBrush` 30% 透明）、turn 头=`Header`+`Status`+`Elapsed`、children 缩进 16px。
- 状态：`Working`→spinner；`Done`→✓；`Error`→❌；`AwaitingApproval`→⏳。
- 审批卡、计划面板挂在 turn 级。

### B2. 工具步"动词+对象"一行
- 折叠态：`<动词图标> <ToolName>(<主参数>)` + ✓/⏳。
- 主参数选取：`Input` 第一个标量值（`city`/`symbol`/`path`/`command` 等键优先），截断 30 字符。
- 展开态：A1 的结果渲染。

### B3. 计划面板独立
- `todos_*` 不再插 transcript 卡；改为更新 turn 顶部的 `LivePlan` 面板（横向滚动或纵向 checklist），完成项划线。
- 面板常驻到 turn 结束，不被工具/答案滚走。

---

## 6. Tier C：进阶（按需）
- 文件编辑类工具 → diff 视图（`+`/`-` 行着色）+ 接受/拒绝按钮（与审批流复用）。
- 每 turn 折叠/复制按钮。
- turn 头显示 token/耗时（接 `UsageContent`）。
- 长答案自动生成"步骤目录"跳转。

---

## 7. Mockup 对比

**现在（工具步 + 答案流式）：**
```
┌────────────────────────────────────┐
│ 🤔 正在思考…                         │
│ ## 北京天气\n\n今天晴，气温 28°C…   │  ← 裸 markdown 源码
└────────────────────────────────────┘
┌────────────────────────────────────┐
│ 🔧 GetWeather  city=北京  ▸          │
│  输入 { "city": "北京" }             │
│  输出 { "result": "北京：晴" }       │  ← 裸 JSON
└────────────────────────────────────┘
```

**Tier A 后：**
```
│ 🔎 GetWeather(北京)  ✓               │
│    北京：晴                           │  ← 一行主结果
│ ## 北京天气                           │
│ 今天晴，气温 28°C…                    │  ← 实时 markdown，无源码
```

**Tier A+B 后（turn 分组）：**
```
┌ 你: 帮我查北京天气          ✓ 2.1s
┊  💭 思考 · 0.8s ›                     │  ← 折叠淡色
┊  🔎 GetWeather(北京)  北京：晴        │
┊  ## 北京天气                           │
┊  今天晴，气温 28°C…                    │
└
```

---

## 8. 实施路线（供后续动手参考）

### 涉及文件
- `ChatMessageItem.cs` —— 加 `ToolKind`/`ResultText`/`ResultKind`/`IsResultLong` 字段 + 计算属性。
- `MainViewModelBase.cs` —— `HandleToolEventSegmented` 归一结果；答案段流式绑定 markdown；turn 分组（Tier B 改 `Messages` 类型）。
- `ChatView.axaml` —— 工具卡按 `ResultKind` 多模板；推理卡折叠淡色；答案段用 markdown 控件流式；turn 容器模板（B1）。
- `SharedTheme.axaml` —— 推理卡淡色样式、turn 竖线、终端块样式。
- 新增 `ToolVerbIcons.cs`（动词图标映射）、`NormalizeToolResult` 辅助（可放 `ChatMessageItem` 静态方法）。
- `Converters/` —— `ToolKindToIconConverter`、`ResultKindToTemplateConverter`（或直接 `IsVisible` 多分支）。

### 依赖与风险
- **A2 流式 markdown 性能**：`Markdown.Avalonia.Tight` 每 token 全量重渲染可能卡；需 Dispatcher 节流（50ms）。若仍卡，退化为"流式时纯文本、结束时 markdown"（比现状好的点：流式文本用正文字体而非等宽，且不显示 markdown 源码符号——可选择性隐藏 `#`/`*`）。
- **B1 `Messages` 类型变更**：影响面大（滚动、持久化恢复、远程消息）。建议 B1 单独一个 PR，先做 A。
- **结果归一靠 key 猜测**：各工具 output 结构不统一，`NormalizeToolResult` 的 key 优先级需按实际工具调优；兜底回退 JSON。
- **不破坏持久化**：turn 容器、结果归一都是 UI 态，`PersistTurnAsync` 仍只存 `Message`（tool/reasoning/answer），`FromTranscriptMessage` 不变。

### 建议顺序
1. **A3**（推理卡折叠淡色）—— 最小、立竿见影。
2. **A1**（工具结果按类型渲染）—— 核心。
3. **A2**（流式 markdown）—— 有性能风险，单独验证。
4. **B2**（动词+对象一行）—— 配合 A1。
5. **B3**（计划面板独立）。
6. **B1**（turn 分组）—— 最大，最后做。
7. **C** 按需。

### 验证
- 构建后用真实 LLM 跑各工具（Notes/CTP/Trading/Qifu/Skills），确认结果渲染规约覆盖。
- 长结果（大文件读取、回测日志）确认折叠+限高生效。
- 推理模型（GLM）确认思考折叠、答案实时 markdown。
- 回归：持久化/恢复、审批流、workflow 路径不受影响。
