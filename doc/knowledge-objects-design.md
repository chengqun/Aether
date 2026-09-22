# 知识对象（Knowledge Objects）设计文档

> 状态：现行设计（2026-09-22 整理）。
> 权威来源：代码（`DocumentKnowledgeTool.cs` / `KnowledgeObjectDefinition.cs` / `OkTargetResolver.cs` 等）+ CLAUDE.md「Knowledge objects」节。本文档是设计综述，代码以仓库为准。

---

## 1. 是什么 / 解决什么问题

知识对象是 labs 资料库中的**微型知识图谱节点**：每个对象是一个 `.md` 文件（YAML front-matter + markdown 正文），带分类 `kind` 和若干**带 role 的图边**（links）指向其它知识对象。它是最简通用知识模型——不是数据库、不是向量库、不是笔记应用，而是「**结构化参照知识的最小可复用单元**」。

它解决的三个问题：

| 问题 | 单体 system prompt | 知识对象 |
|---|---|---|
| **重参照数据复用** | 完整字段表/规则库每次都进 prompt | 按需 `read` 才进上下文（按需付费） |
| **一处改、处处新** | 改 schema 要改 N 个 agent 的 prompt | 资产节点改一次，所有引用它的流程下次 read 拿新版 |
| **新场景搭建** | 复制粘贴 prompt | 新建流程节点 + 勾选既有资产 |

**与其它扩展机制的选型**（见 CLAUDE.md「Agent 扩展能力」）：

| 机制 | token 成本 | 适用 |
|---|---|---|
| tools | 每轮带全部工具 schema（随数量增长） | 业务动作 |
| MCP | 同上 + 跨进程 | 外部能力 |
| skill | 每轮只带 name/desc 索引，`load_skill` 取正文 | 流程知识 + 轻量脚本 |
| **知识对象** | 索引都不常驻，`read` 才进上下文 | **结构化参照知识（表 schema / 规则库，需跨流程复用）** |

---

## 2. 核心设计理念

1. **最简通用模型**：一个对象只有 `kind` + `links` + `body`（+ front-matter 额外字段透传）。typed graph links（带 role 的有向边）是它的**定义性特征**——这是它区别于普通 markdown 文件的全部结构。
2. **按需付费**：对象内容不进任何常驻上下文；agent 先 List/Search 发现，再 `read` 拉正文。读是非破坏的、per-flow 的。
3. **路径即指针**：文档版对象的权威引用是**相对路径字符串**（`targetId`），没有 ID 注册表、没有数据库反查。相对路径本身就是指针。
4. **语料规范归 README 散文**：各项目目录的 README 承载该项目的 kind 词表/边方向等规范（agent 可 read，天然按目录一份、各目录不同）；平台只做结构化检查（断链）。曾用 `knowledge-manifest.yaml` 机器可读清单承载语料规则，2026-09-22 移除（见 §8）。
5. **DAG 纪律**：边**单向**，只写「流程 uses/then → 资产」这类正向依赖边；**禁止反向回指边**（`consumed-by`/`schema-for` 等——冗余，且会被 read 内联当噪音）。分层：**流程层**（`kind=流程`，带出边，是消费/编排者）vs **资产层**（`kind=数据源`/`规则`，被引用的复用目标）。

---

## 3. 数据模型

`business/Notes/Acme.Notes.Domain/Knowledge/KnowledgeObjectDefinition.cs`：

### KnowledgeObjectDefinition（内存模型）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Kind` | `string` | 分类，自由字符串（流程/数据源/规则/Entity/已知认知…）。无强制词表（manifest 可选声明） |
| `Links` | `List<KnowledgeLink>` | typed 引用列表（图边） |
| `Body` | `string` | markdown 正文：人类说明、字段表、SQL 模板、示例输出等 |
| `ExtraFrontMatter` | `Dictionary<string, object?>?` | front-matter 未知键**透传**（kind/links 之外的全部顶层键，如 Intelligence 生命周期 subject/claim/status）。解析时装载、序列化原样写回——表单/agent 编辑**不丢语料自有元数据** |

### KnowledgeLink（一条图边）

| 字段 | 类型 | 说明 |
|---|---|---|
| `Role` | `string` | 边类型，自由字符串（uses/then/applies-to/produces…，只写正向边） |
| `TargetId` | `Guid?` | note 版遗留字段；**文档版恒为 null**（权威引用是路径，存 Target） |
| `Target` | `string` | 文档版=目标**相对路径**（权威）；旧数据=标题（回退匹配） |
| `Expand` | `bool` | 作者标记「AI 读取此对象时内联该目标内容」。默认 false=只列引用、agent 按需单独 read（见 §7） |

### 文件格式（labs `knowledge/*.md`）

```markdown
---
kind: 流程
links:
- role: uses
  targetId: Hive表/fin_dw.dwd_refund
- role: then
  targetId: ../规则/退款判定规则
  expand: true
subject: 退款会员日志分析        # ← kind/links 之外的键 = ExtraFrontMatter 透传
status: active
---
# 正文（markdown）
退款流程第一步…
```

- front-matter 解析：YamlDotNet camelCase + `IgnoreUnmatchedProperties` + 未知键经 `FrontMatterExtras` 行级拆分保序装载（`DocumentKnowledgeSourceParser`）。
- `name` = 文件名 stem（**不在内容里**）；无 front-matter 容错（全文当正文）；有起始无结束 `---` 容错。
- round-trip 稳定：序列化把 `Target` 写回 `targetId` 字段，保持用户既有格式（真实语料验证 121/121 文件零丢失）。
- legacy `.ok` 后缀兼容（解析层 `.md` 优先、`.ok` 回退；2026-09 从 `.ok` 迁移为 `.md`）。

### 存储位置与身份

- labs 子目录 `knowledge/`（`NoteNodeType.Knowledge = 3`，目录映射权威在 `CatalogNoteDirectory`）。
- **文件即权威**：无 note SQLite、无第二套存储（note 子系统 2026-09 整体删除）。
- **现役语料（Qifu 租户，2026-09-22 核实）**：`~/Library/Application Support/Aether/Desktop-Qifu/labs/knowledge/`（Windows 为 `%LOCALAPPDATA%\Aether\Desktop-Qifu\labs\knowledge`，路径单一来源 `TenantDataPaths`）——113 个 `.md`、0 个 `.ok`，本体是独立 git 仓库（agent 经 git 工具 + `GetCurrentKnowledgeDirectory` 可直接对其提交）。结构按项目分区：`企业知识架构/{1_Scenario, 2_Feature, 3_Method, 4_Procedure, 5_Assets}` 数字分层目录 + 45 个目录化 README 容器。
- 知识库根 = `IKnowledgeDirectoryProvider.CurrentDirectory`（知识对象页打开的目录）?? 默认 labs 根。**独立单例**，与文档页 `ICurrentProjectDirectoryProvider` 解耦（防知识库目录覆盖项目目录，SCM/终端/活动页都订阅后者）。
- 场景目录化惯例：**目录即对象、README.md=本体**——`ResolveScope` 命中目录时追加 `<dir>/README.md` 候选；给目录 README 声明 kind/links 可使其成为链接到成员的一等知识对象。

---

## 4. AI 工具面（桌面 9 工具 + 1 相邻）

全部注册在宿主层 `apps/Desktop/Acme.Desktop.Shared/Hosting/DesktopToolRegistrations.cs` 的 `AddDocumentKnowledgeTool()`（`AddDesktopCommon` 调用），类别「文档知识」。工具类 `DocumentKnowledgeTool`（`Shared/Shell/Documents/`）。

> **为什么住宿主层而非 `ai/` 适配器**：工具需要 `IKnowledgeDirectoryProvider`（apps 层 `Acme.Desktop.Mvvm`），而 `ai/` 适配器不可引 apps/——同 `GetCurrentProjectDirectoryTool`/`BuildTool`/`RunInTerminalTool` 模式。

| # | 工具名 | 审批级别 | 签名 | 行为要点 |
|---|---|---|---|---|
| 1 | `ListDocumentKnowledgeObjects` | **Safe** | `(folder?)` | 递归枚举 `knowledge/` 子树 `.md`（上限 200；跳 bin/obj/.git/node_modules + 符号链接目录），返回路径/stem/kind/80 字正文预览。folder 可带可不带 `knowledge/` 前缀 |
| 2 | `ReadDocumentKnowledgeObject` | **Safe** | `(name, expandDepth?)` | 读完整定义：kind + 正文 + links 内联展开（见 §7）。name=相对路径（带/不带 `knowledge/` 前缀、带/不带 `.md` 后缀均可） |
| 3 | `CreateDocumentKnowledgeObject` | **Confirm** | `(name, kind, body, linksJson?, extraJson?)` | labs 模式**始终落 `knowledge/` 子树**（前缀幂等）。links JSON 数组（`targetId` 优先，回退 `target`；每条可 `expand:true`）。extraJson=front-matter 额外字段（标量）。已存在则失败 |
| 4 | `UpdateDocumentKnowledgeObject` | **Confirm** | `(name, kind?, body?, linksJson?, extraJson?)` | 部分更新：传哪个改哪个。**links 全量替换**；**extraJson 按键合并**（未提到的键透传保留——只改 status 不丢 subject/claim） |
| 5 | `SearchKnowledge` | **Safe** | `(query, kind?, folder?, limit=20)` | 跨目录关键词搜索：不区分大小写子串匹配 stem/kind/额外字段值/正文，按命中位置排序 name(4)>kind(3)>extras(2)>body(1)。上限 50。List 是枚举、Search 是检索 |
| 6 | `RelatedKnowledge` | **Safe** | `(name)` | 出边（它引用谁，含解析后路径）+ **入边**（谁引用它，反向链接——全库扫描）。合并/改名前查影响面 |
| 7 | `RenameKnowledge` | **Confirm** | `(name, newPath)` | 移动文件 + **自动改写全部入边引用**（改写先于移动——中途失败不丢引用）。改写为规范 targetId（`KnowledgeLinkRewriter.CanonicalTargetId`：同目录裸名/跨目录 `knowledge/` 前缀根相对）。README.md 目录对象不支持 |
| 8 | `MergeKnowledge` | **Confirm** | `(primary, secondary, newName, mergedBody, kind?, extraJson?)` | **Knowledge Marriage**（见 §9）：新对象 C 带 `parents=[A,B]` 溯源，links 取并集（role+target 去重、剔自指），原对象标 `status: superseded` + `superseded_by=C` **保留不删**（lineage），入边自动改指 C |
| 9 | `ValidateKnowledge` | **Safe** | `()` | **断链体检**（见 §8）：全量解析 `.md`，links 解析不到目标文件 → error。写完/改完（尤其 Rename/Merge 后）自检 |
| 相邻 | `GetCurrentKnowledgeDirectory` | **Safe** | `()` | 获取资料库目录绝对路径与顶层结构（agent 跑 git 等目录参数用，如 `git_commit` 的 workingDirectory） |

**审批级别设计**：读（List/Read/Search/Related/Validate）= Safe 自动跑；写（Create/Update/Rename/Merge）= Confirm 弹审批——对齐全仓「reversible write = Confirm」惯例。专家编辑器可按 agent 覆盖。

### 手机端子集（MobileDocumentKnowledgeTool）

`apps/Mobile/Acme.Mobile.Shared/Hosting/Tools/`——**4 工具子集**：List/Read/Create/Update（无 extraJson、无体检/检索/治理三件）。**永不退休**（iOS 禁子进程，与桌面弃 Python skill 的决策无关）。

---

## 5. 作用域与守卫（防误读写）

`DocumentKnowledgeTool.ResolveScope` + 三道守卫：

1. **作用域解析**：labs 根含 `knowledge/` 子目录 → 只操作该子树（isLabs=true）；自定义知识库目录（无该子目录）→ 回退整目录（向后兼容）。
2. **越界守卫**（Read/Update/Rename/Merge）：候选路径解析后必须 `IsUnder(knowledgeRoot)`——指向 `agents/*.agent.md`、`rules/*.rule.yaml` 等非知识对象 `.md` → 拒绝（防 AI 把 agent 文件/README 误当知识对象读写）。
3. **遍历守卫**：跳 bin/obj/.git/node_modules 等忽略目录 + 符号链接目录（防环）；枚举上限 200（List）/ 2000（体检/改写收集）。

`knowledge/` 前缀幂等剥离（`StripKnowledgePrefix`）：裸路径 `业务分析/退款` 与带前缀 `knowledge/业务分析/退款` 等价。

---

## 6. link 路径解析（OkTargetResolver，四层候选）

`apps/Desktop/Acme.Desktop.Mvvm/OkTargetResolver.cs`（共享静态 helper，文档编辑器图解析 / AI 工具 / 体检**三处同源**——防解析语义分叉产生假阳性断链）。按优先序：

1. **路径候选**：labs 根相对（权威写法）+ 相对源文件目录（`./b`、`../xx/b`）。基路径是**目录**时追加其 `README.md` 候选（场景目录化惯例）。
2. **knowledge/ 子目录基**：labs 模式下补「知识库根相对」候选（不带 `knowledge/` 前缀的写法）。
3. **裸名全局兜底**（仅路径候选全 miss 且 targetId 为裸名时）：①stem 全等 ②stem **后缀**等（语料「库名前缀」文件命名约定：链接写 `loan_vintage_base_a`，文件叫 `dp_data_db.loan_vintage_base_a`）。全等 > 后缀，同类内取路径序最小（确定性）。
4. **去点后缀 × 祖先目录兜底**（解析意图恢复）：真实语料相对链接普遍「`../` 计数不可靠 + 根相对后缀」——去点后的后缀在源文件某祖先目录下唯一存在时，逐级上溯（最近祖先先，最保守解释；上限 12 级）。

> 第 3、4 层是 2026-09-20 的「解析意图恢复」修复成果：真实语料断链 65 → 1（唯一剩的是指向非 KO 实跑产物的真断链）。

---

## 7. 读取展开策略（Read 的 expandDepth）

`KnowledgeExpansionBuilder`（`Mvvm/Knowledge/`，纯函数零 I/O 可单测，解析目标经注入委托）：

- **作者标志模式**（`expandDepth=null`，默认）：只内联 link 上标了 `expand: true` 的目标，沿标过的链递归展开多层；未标的边只列在 links 清单（agent 按需单独 read）。**默认翻转语义**：既有文件无 expand 字段 → 全 false → 只返回本对象（body + links 清单），契合「当前页本身才重要」。
- **覆盖模式**（`expandDepth=N`，0~4）：忽略作者标志，blanket 内联 N 层全部子节点（N=0 仅本对象；N=1 等价旧行为）。agent 临时挖深用。

**安全阀**（防上下文溢出——多目标内联曾撑爆 LLM 上下文窗口触发 GLM `ResponseEnded` 提前终止）：

| 阀 | 值 |
|---|---|
| 递归深度硬上限 | 4 层 |
| 单目标正文内联上限 | 8000 字符（截断头部） |
| 累计内联正文上限 | 20000 字符 |
| 防环 | 按全路径去重（菱形依赖只展开一次；焦点路径预入 visited） |

front-matter 透传字段（ExtraFrontMatter）也输出进头部——agent 读认知对象能看到生命周期元数据。

> **现役语料现状（2026-09-22）**：113 个文件 **0 个 `expand:` 标志**——作者标志从未被语料采用，默认 read 即「只返回本对象（body + links 清单）」；agent 要挖深须显式传 `expandDepth`。旧行为描述曾滞留 CLAUDE.md/工具注册描述/知识库自述 README 三处，同日已同步修正（见 §14 Gotcha ⑩）。

---

## 8. 断链体检（ValidateKnowledge）

`ValidateKnowledge()`（Safe）：全量解析知识对象 `.md`，检查**断链**——link `targetId` 解析不到任何知识对象文件 → error（改名/移动/删除目标后遗留的坏引用）。

- **解析与 Read 完全同源**（`OkTargetResolver.ResolveTargetWithPath`：路径候选 + 裸名全局兜底 + 目录/README 兜底）——防校验器与读写工具解析语义分叉产生假阳性断链。
- 解析失败的文件（Def=null）仍参与断链索引（可被别的文件解析到）、自身 links 不检查（不可信）。
- **诊断不阻断写入**——自检信号非门禁。写完/改完（尤其 Rename/Merge 改写引用后）调用。

**manifest 机制（2026-09-22 已移除）**：曾配套 `knowledge-manifest.yaml`（kinds 词表 / edges 边方向矩阵 / requiredFields 必填字段，三类**软警告**；`KnowledgeManifestLoader` 装载 + `KnowledgeGraphValidator` 检查）。移除原因：

1. 三项均软警告、不阻断；唯一 error 级检查（断链）与清单无关——体检的真正价值独立存在。
2. 维护成本高于收益——语料演化快于规则维护：历史体检发现 kind 漂移 36 处，最终选择删清单而非按词表收敛（语料重构 commit `e933fb3` 先行删了清单，当天机制随之移除）。
3. 「每目录规则不同」的实际承载是**各项目 README 散文**（agent 可 read，天然按目录一份；语料 README 明写「kind 由各项目自行定义并在项目 README 中说明」）——机器可读清单要再加就近目录解析/作用域/合并语义，换 advisory 警告，不值。

移除后工具描述与输出不再含 manifest 概念——此前 agent 每次体检看到 `ManifestLoaded: null` + 描述提及就复述「manifest 不存在」的噪音源一并清除。

---

## 9. 知识增长循环与治理

### 冷启动/写循环（工具面闭环）

```
SearchKnowledge（Explore 步：关键词发现既有知识）
  → ReadDocumentKnowledgeObject（读全文 + 内联）
  → CreateDocumentKnowledgeObject / UpdateDocumentKnowledgeObject（写/部分更新）
  → ValidateKnowledge（自检断链/词表/必填字段）
```

### 治理三件（结构演化）

- **RelatedKnowledge**：改名/合并/废弃前查 **Incoming** 评估影响面（谁引用我）。
- **RenameKnowledge**：移动文件 + 全库入边自动改写（**先改写后移动**——旧路径仍可解析时完成改写，中途失败不丢引用）。规范写法由 `KnowledgeLinkRewriter.CanonicalTargetId` 决定：同目录→裸名，跨目录→`knowledge/` 前缀根相对。front-matter 经透传 round-trip（额外字段不丢）。
- **MergeKnowledge = Knowledge Marriage**（两对象表达同一概念时）：
  - 生成新对象 C：front-matter 带 `parents=[A,B]` 溯源；links = A∪B（role+target 去重、剔解析后指向 A/B 自身的自指边）；`mergedBody` 由 agent 融合两正文写成（先 Read 两者）。
  - A/B **不删**：标 `status: superseded` + `superseded_by: C` 保留 lineage/provenance。
  - 全部入边引用自动改指 C。
  - 改写核心：`RewriteReferencesAsync`（全库扫描逐 link 解析，命中旧路径的改写为规范 targetId）。

---

## 10. 分层架构

```
business/Notes/Acme.Notes.Domain/Knowledge/          ← 纯模型 + 纯校验器
  KnowledgeObjectDefinition / KnowledgeLink / KnowledgeGraphValidator / KnowledgeManifest
business/Notes/Acme.Notes.Infrastructure/Knowledge/  ← 解析/序列化/清单装载（YamlDotNet）
  DocumentKnowledgeSourceParser / KnowledgeSourceParser / FrontMatterExtras / KnowledgeManifestLoader
apps/Acme.Desktop.Mvvm/                              ← 解析助手 + 展开器 + 改写器 + VM（UI 无关）
  OkTargetResolver / KnowledgeExpansionBuilder / KnowledgeLinkRewriter
  DocumentKnowledgeViewModel / IKnowledgeDirectoryProvider
apps/Desktop/Acme.Desktop.Shared/                    ← AI 工具（宿主层）+ View + 注册
  Shell/Documents/DocumentKnowledgeTool.cs
  Hosting/DesktopToolRegistrations.cs（AddDocumentKnowledgeTool）
apps/Mobile/Acme.Mobile.Shared/Hosting/Tools/        ← 手机端 4 工具子集
  MobileDocumentKnowledgeTool.cs
```

依赖方向单向向下；Domain 零依赖（校验器目标解析以委托注入，不做文件 I/O）。

---

## 11. 与 Agent 的混合使用（推荐模式）

- **硬契约进 agent body**：必须每次遵守的输出格式、表范围约束等写 agent 文件正文（system prompt），确保可靠。
- **重参照数据进知识对象**：完整字段表、规则库、SQL 模板、示例输出抽成 KO 按需 read。agent body 里一句「先用 `ReadDocumentKnowledgeObject` 读『XX流程』」即可。
- **复用**：一个资产节点被多个流程 `uses`；改一处 schema，所有引用流程下次 read 拿新版——单体 system prompt 做不到。新场景 = 新流程节点 + 勾资产 + 少量专属规则。

---

## 12. UI（知识对象 nav 页）

`PanelKeys.Knowledge`（两租户都有，nav 第 2 位）：`KnowledgeObjectsViewModel`（Mvvm）+ `KoTreeView`（shell 级左栏）+ `KnowledgeObjectsView`（右栏）。

- **两栏布局**：上=`DocumentKnowledgeView` 表单（kind + links 文本行 + body 预览），下=AvaloniaEdit 源码；**双向同步**（两层守卫防回环）+ Ctrl+S 落盘。
- link 行只显示目标 **stem**（目录指针隐藏在源码）；`Build` 重建完整 targetId。
- **图视图**：`BuildGraphModel`/`BuildMermaid` 经 `KnowledgeGraphBuilder.BuildClosure` + `WorkflowGraphBuilder.Layout` 弹 Mermaid 拓扑图；link 解析失败→断链仍画（叶子节点、不跟出边）。
- **实时重载**：`FileTreeVmBase` 共享 `FileSystemWatcher`——AI 工具写盘后树 + 活动 `.md` 自动刷新，无需重启。

---

## 13. 设计决策与演化史

| 时间 | 决策 |
|---|---|
| 2026-09 前 | 文件后缀 `.ok`；note SQLite 子系统并存（`note_node` 表） |
| 2026-08-31 | **Read 展开模式升级**：`expandDepth` 覆盖参数 + `KnowledgeLink.Expand` 作者标志——默认从「内联一层」翻转为「无标志=只返回本对象」，安全阀（深度/字数上限/防环）同批引入 |
| 2026-09 | **`.ok` → `.md` 迁移**（解析层 `.md` 优先、`.ok` 回退）；**note SQLite 子系统整体删除**——labs 文件成唯一权威存储，无第二套入口 |
| 2026-09-20 | **front-matter 未知键透传**（修数据丢失 bug：typed DTO + IgnoreUnmatchedProperties 会静默剥掉 Intelligence 生命周期字段）；**manifest 体检**；**OkTargetResolver 四项解析增强**（断链 65→1） |
| 2026-09-20 | **知识增长循环**：Search/Related/Rename/Merge 四工具 + Knowledge Marriage |
| 2026-09-21 | **全面弃用 Python skill**：桌面 C# 九工具是唯一知识对象工具面（曾短暂退休改走 ko.py skill，随即恢复并定局）；`~/Desktop/knowledge-objects/`（ko.py 六命令五类型契约）归档零引用；`knowledge-bootstrap`/`knowledge-object-conventions` 两 skill 同批删除。**手机端 `MobileDocumentKnowledgeTool` 从未退休**（iOS 禁子进程） |
| 2026-09-22 | **Qifu 语料大重构**（Aether agent 执行，语料库 git `e933fb3`）：删 `6_Intelligence`/`Glossary`/skills/**knowledge-manifest.yaml**，已知认知（共享设备tag客群、prepay_vintage 结构、大盘水位基线等）下沉至对应场景课题目录，检索链路去 Glossary 消歧——体检回到「仅断链」，词表/边矩阵软约束退场 |
| 2026-09-22（下午） | **manifest 体检机制整体移除**（代码侧）：`KnowledgeManifestLoader` 删除、`KnowledgeManifest`/`KnowledgeEdgeRule` record 删除、`KnowledgeGraphValidator.Validate` 去掉 manifest 参数只留断链、工具描述/输出清除 manifest 概念——软约束维护成本高于收益，语料规范归各项目 README 散文（见 §8） |

当前定局：**桌面 = C# 九工具 + 断链体检/检索/治理全家桶；手机 = 4 工具子集；skill 路径已死；manifest 机制已移除（体检=纯断链）**。

---

## 14. 注意事项（Gotchas）

1. **`targetId` 是相对路径字符串不是 Guid**——文档版权威引用是路径。三种写法：知识库根相对（权威）/ 相对源文件目录（`./b`、`../xx/b`）/ 裸名（全局 basename 匹配）。目录内互链推荐裸名/相对路径（目录迁移不断链）。
2. **link 靠路径解析须路径有效**——改名/移动文件经 `RenameKnowledge` 自动改写入边；手动 git mv 后须跑 `ValidateKnowledge` 复查（README.md 目录对象不支持工具改名）。
3. **links 全量替换、extraJson 按键合并**——Update 只传 body 时 links 不动；只改 status 时 subject/claim 不丢。
4. **越界拒绝**：Read/Update 只接受 `knowledge/` 子树内的文件——`agents/*.agent.md` 等非知识对象 `.md` 会被拒（防误读）。
5. **反向边是噪音**：写反向回指边（consumed-by 等）会被 read 内联当噪音；只写正向边（见 §2 DAG 纪律）。
6. **展开有安全阀**：read 默认只内联 `expand:true` 的边；覆盖模式最多 4 层、单目标 8000 字、累计 20000 字——超限截断并提示单独 read。
7. **extraJson 仅标量**：嵌套 JSON 对象/数组跳过（front-matter 是 YAML，嵌套原样写回会产非法 YAML）。
8. **AI 写盘实时生效**：`FileSystemWatcher` 自动重载树 + 活动 `.md`——agent 编辑知识对象后无需重启。
9. **manifest 已移除（2026-09-22）**：`ValidateKnowledge` 只做断链体检（error 级、不阻断写入）；kind 词表/边方向等语料规范看各项目 README。工具描述/输出不含 manifest 概念——agent 不应再提它（此前因描述提及 + `ManifestLoaded: null` 输出字段被反复复述「manifest 不存在」）。
10. **Read 默认已于 2026-08-31 翻转**（commit `9d206f8`）：旧默认「内联一层」→ 现行作者标志模式（无标志=只返回本对象，agent 传 `expandDepth` 挖深，见 §7）。旧行为描述曾滞留三处（CLAUDE.md / `DesktopToolRegistrations` 注册描述 / 知识库自述 README），2026-09-22 已同步修正——知识库 README 的「乱挂 links 污染上下文」告诫同步改为指向 `expand` 标志（上下文成本所在）。
