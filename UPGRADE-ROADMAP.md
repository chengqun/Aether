# Aether 升级改造路线图

> 生成：2026-09-14 · 基线：main @ f140c5f + CPM 基础设施现代化（本轮已落地，见第一节）
> 用法：按优先级批次执行；每项标注了现状证据与影响面。动手前先读对应 CLAUDE.md 章节。

---

## 一、已完成（2026-09-14 本轮）

| 项 | 结果 |
|----|------|
| **CPM 中央包管理** | `Directory.Packages.props`（133 包唯一权威）+ 85 个 csproj 剥离 289 处内联 Version |
| **Directory.Build.props** | 仅 CPM 开关（TargetFramework/Nullable 保持各项目自持——server 10 项目未声明 ImplicitUsings，全局强制会翻转行为） |
| **externals/ 守卫** | `externals/Directory.Build.props` + `Directory.Packages.props` 拦截向上查找，Lean/MAF/FlaUI-MCP/Ctp.Net.Core 构建行为零变化 |
| **nuget.config** | 仓库级单源（官方），消除 NU1507，本地/CI 对齐 |
| **包版本分裂统一** | 6 处：`Logging.Abstractions`/`DI(.Abstractions)`→10.0.11（MAF 1.20 链）、`Hosting`/`Http`/`SignalR.Client`→10.0.10 |
| **Roslyn 对齐** | `Microsoft.CodeAnalysis.CSharp` 4.8.0→4.13.0（Trading.Scripting / Trading.Runtime.Lean / Harness.Scripting），消除同进程双版本共存 |

**后续约定**：加/升包 = 只改 `Directory.Packages.props` 一处；新增顶层目录若含 csproj 需自查是否被根 props 覆盖；externals/ 新增 submodule 无需处理（守卫已隔离）。

**收尾待办**：CLAUDE.md「Build, test, run」节补一段 CPM 约定（加包改哪里、externals 守卫的存在理由）。

---

## 二、已知技术债 —— 已全部结清（2026-09-14 逐项核验）

> 来源：`non-apps-audit-findings` 与 `avalonia-perf-audit` 两轮审计记录。**本节已于 2026-09-14 逐项对照当前代码核验——原列的 P0/P1 全部已修**，故从「待办」降级为「已完成」归档，下表保留证据锚点备查。

| 原列项 | 核验结论 | 证据 |
|--------|---------|------|
| **P0 Lean 全局静态无运行时互斥** | ✅ 已修 | `LeanGlobalLock`（`SemaphoreSlim(1,1)`）由三个入口点共享，UI 层 `CanRun`/`CanStartLive` 交叉检查退为兜底 |
| P1 会话泄漏链 | ✅ 已修 | `HarnessRuntime.EvictSession` + `ICustomAgentRunner/ICustomWorkflowRunner.EvictConversation` + `ConditionalWeakTable` 弱键自动回收 |
| P1 Roslyn 可卸载 ALC 残余 | ✅ 已修 | `ConcurrentDictionary<hash,(Assembly,CollectibleAlc)>` + `UnloadStale`；Strategy/Rule/Lean 三套编译器各自处理（Lean 因 `LoadFrom` 类型身份约束只能磁盘清理，已随带） |
| P1 并发竞态点（审计清单） | ✅ 已修 | 含 `UNIQUE(conversation_id, seq)` 事件流 TOCTOU 守卫 + rowid 去重（`SqliteConversationStore.MigrateAsync`） |
| P1 流式 markdown 重解析卡顿 | ✅ 已修 | 流式期纯文本 `TextBlock`（不解析 markdown），`FinalizeSegment` 一次性渲染；VM 侧 150ms `flushTimer` 排空有序块队列 |
| P1 SettingsVM 事件泄漏 | ✅ 已修 | `SettingsViewModel.Cleanup` 退订 |

**本批（2026-09-14）新发现并修复的真剩余项**（均不在原审计清单内，本轮验证式排查发现，各带回归测试）：

| # | 项 | 证据 / 修法 | 测试 |
|---|----|-----------|------|
| 1 | **`AsyncSqlQuery` 未标审批级别 → 任意 SQL 自动放行** | `ai/Qifu/.../QifuAIBuilderExtensions.cs:44` 缺 `ToolApprovalLevel` → 默认 `Safe`；对齐同级 Dangerous 工具显式声明 | `tests/Acme.Qifu.AI.Tests`（新建，3 例） |
| 2 | **`CurrentUserResolver` 不认 base64url → JWT `sub` 从未被提取** | `CurrentUserResolver.cs:32,50-51`：JWT payload 是 base64url（`-`/`_`），旧 `PadBase64` 只补 `=` 不换字符 → `FormatException` 被吞 → 恒回退 MD5(username) | `CurrentUserResolverTests`（4 例，含 fixture 前置断言） |
| 3 | **`ApplyPatchTool` lineEdit insert 差一** | `ApplyPatchTool.cs:275` 将 1-based `start` 当 0-based 索引用（delete/replace 均 `start - 1`）→ 插到下一行 | `ApplyPatchToolTests` +3 例 |
| 4 | **Ctp 下单/撤单工具 `requireConfirmation=true` 默认参数 + 无 try/catch** | `OrderInsertTool.cs:20` / `OrderActionTool.cs:19`：审批闸门本就在注册层（Dangerous），工具内再默认「待确认」→ 通过审批后仍只回一句 no-op；改默认执行 + 显式 `dryRun` 开关 + try/catch 结构化错误 | `tests/Acme.Ctp.AI.Tests/OrderToolTests.cs`（6 例） |

**仍在案（低优先级，未修）**：`TeamsMessageTool` SSE 分支不检查 JSON-RPC `error` 字段 → 失败时仍返回「已通知」。


---

## 三、大文件重构（架构债，已锚定「下轮大功能」）

| 文件 | 行数 | 拆分方向 |
|------|------|---------|
| `apps/Desktop/Acme.Desktop.Mvvm/ChatWorkspaceViewModel.cs` | 1980 | 纵向拆 4 块：会话持久化（PersistRun/BuildTurns）→ `ConversationPersistenceService`；工具事件/diff/撤销 → `ToolEventHandler`；审批协调（TryAutoApprove/4 档拦截）→ `ApprovalCoordinator`；手机远程控制（补拉/队列/摘要）→ `RemoteMessageProcessor` |
| `apps/Desktop/Acme.Desktop.Mvvm/KnowledgeObjectsViewModel.cs` | 1413 | 按 NoteNodeType 工厂职责拆分；CsvTable/CsvPivot（~880 行）已是独立类，主 VM 可继续瘦身 |
| `apps/Mobile/.../AgentChatDetailViewModel.cs` | 1397 | 镜像桌面拆分思路（流式分段已对齐桌面，职责同构） |
| `apps/Mobile/.../ChatDetailViewModel.cs` / `ChatViewModel.cs` | 931 / 898 | 社交聊天 VM，与 Agent VM 共享消息基建后各自瘦身 |
| `apps/Desktop/.../MermaidRenderer.cs` | 906 | 渲染管线 vs 图布局（dagre/StraightenEdges 后处理）分层 |

**原则**：纯机械移动 + 接口不变，不动行为；每拆一块跑一次全量测试。

---

## 四、功能补齐（已拍板待做，按 CLAUDE.md 设计执行）

### 交易 P2：真实 CTP 委托 ★ 有完整设计
- **现状**：`CtpBrokerage.PlaceOrder`（`business/Trading.Runtime/Acme.Trading.Runtime.Lean/Brokerage/CtpBrokerage.cs:106`）拦截下单返回 false，tick→bar→信号链路已通。
- **改造**（CLAUDE.md「Trading + Lean」节 P2 段有完整方案）：`PlaceOrder` 映射 Lean Order→`CtpOrderRequest` 调 `_session.OrderInsertAsync`（与期货页手动交易同一端口）；`OrderReceived`/`TradeReceived` 事件回译 Lean Order/Trade。零新增依赖、零环（`Ctp.*` 不引 `Trading.*`）。
- **前置**：第二节的 P0 互斥**已落地**（`LeanGlobalLock`），可直接开工。

### Workbench P3/P4（Deferred 项）
- P3 面板 pop-out（聊天/终端/浏览器面板拖出成独立窗口）
- P4 插件系统（`IViewRegistryContributor` 已是轻量插件缝，升级为运行时外部 DLL 加载）

### RunProgress Step2：信号下沉 Harness
- 现状：`RunProgress`/`StreamHandlers` 在 Core，但 `EmitProgress` 构造点在 `ChatWorkspaceViewModel`（VM 层）。下沉到 `HarnessRuntime.RunStreamingCoreAsync` 让所有消费者（含 Automation 定时任务执行路径）免费获得进度信号，桌宠/未来 UI 不再依赖聊天 VM 在场。

### 手机端待办
- **ChatDetailPage 气泡根元素隐患**（`apps/Mobile/Acme.Mobile.Shared/Views/ChatDetailPage.xaml:19,30`）：MineTemplate/OtherTemplate 仍是 `HorizontalStackLayout` 根——iOS CollectionView 右对齐跑版/长消息被裁（`maui-chat-bubble-alignment` 记录在案）。改法照 `DesktopAssistantPage` 的 Grid Fill + 内层 Border End。**低成本高收益**。
- **功能补齐**（移植非新建）：skill 管理 / MCP 配置 / 知识对象编辑 / agent 自举工具——桌面端 `DocumentKnowledgeTool`/`McpManagementTool`/`AgentManagementTool` 镜像到 `Mobile.Shared.HostGlue`（部分已有：`MobileDocumentKnowledgeTool` 存在，查缺补漏）。

### 知识对象 link 选择器
- 文档版 link target 仍是文本输入；做「镜像选择器」（浏览知识库目录选目标文件），替换手敲相对路径（路径权威，改名/移动需手动更新 link 的痛点）。

---

## 五、技术栈升级（低风险批次，CPM 后成本大降）

| 项 | 现状 | 动作 | 风险 |
|----|------|------|------|
| MAF 跟进 | 1.20.0 稳定 + Mcp alpha | 升级=改 `Directory.Packages.props` 一处 + `externals/MAF` submodule 重 pin 对应 tag（CLAUDE.md 要求两者 lockstep） | 低（增量为主） |
| LeptonXLite | 5.4.1 vs ABP 10.4.1（已知不匹配，可运行） | 升级至 10.x 配套，消除 Blazor 管理后台潜在 UI 兼容隐患 | 中（主题 CSS/组件 API 变更，需逐页过 7 个 admin 页） |
| MEAI 10.9.0 | MAF 1.20 传递链 pin | 随 MAF 升级联动，勿单独动 | — |
| `Microsoft.Extensions.*` patch | 10.0.9/10.0.10 混存（Logging.Debug 10.0.9 等） | 顺手统一 10.0.10+（CPM 一处改 + 全量测试） | 低 |
| xunit 2.9.3 / Test.Sdk 17.14.1 | 全仓 15 个测试项目 | 已统一（本轮 CPM 顺带），暂无必要追新 | — |

---

## 六、工程卫生

1. **CLAUDE.md 同步**（本轮收尾即做）：Build 节补 CPM 约定；顺手修一处过时记录——「Stockapp.Mobile android TFM 条件未修，CI 首跑会挂」**已过时**（本次核实两个 Mobile 均已无条件声明 android TFM）。
2. **构建脚本参数化**：`scripts/` 8 个平台硬编码脚本 → 单一入口 `build.sh --tenant stockapp --platform macos`，砍 ~70% 重复。
3. **mcp/ 范例建设**：现仅 SampleServer/EchoServer。可建设为 Aether 官方 MCP server 模板库（.NET 骨架 + `mcp-servers/*.json` 配置模板文档），降低外部工具接入门槛。
4. **CI 冒烟**：CPM 改造后跑一次 `build-qifu.yml` / `build-stockapp.yml` / `deploy-iis.yml` 全绿确认（本地已验证 restore/build/test；CI 环境源配置与本地已对齐）。
5. **README 更新**：解决方案结构表加 `Directory.Packages.props`/`nuget.config` 一行说明。

---

## 七、战略级（需业务决策，勿直接动手）

| 方向 | 说明 | 状态 |
|------|------|------|
| **Lean 出进程** | 回测/实盘引擎移出主进程（gRPC/stdio），根治全局静态共享，解锁并发回测 | 已拍板暂缓，锚 P2 启动（P0 进程内互斥已落地，见第二节） |
| **server/ 模块化瘦身** | ABP 模板 ~109k 行，可评估无前端调用的 AppService/Entity 剥离 | 中风险，无痛点不动 |
| **ChatVM 拆分** | 第三节首项 | 锚「下轮大功能」（P2 交易或手机补齐时顺路做） |

---

## 附：勘误（对 2026-09-12 首轮分析的修正）

| 首轮结论 | 实际情况 |
|---------|---------|
| 「Moq 4.16.1 较旧，评估 NSubstitute」 | **误报**——Moq/NUnit 来自 `externals/MAF` 测试项目，本仓库测试全 xunit，无 Moq |
| 「MAF 1.9.0~1.20.0 碎片化」 | **误报**——旧版本号来自 externals/MAF 源码自引，本仓库 src/ 统一 1.20.0 |
| 「ModelContextProtocol 1.2.0 vs 2.1.0 并存」 | **误报**——2.1.0 来自 externals；本仓库统一 1.2.0（与 `Aether.AI.Mcp` 传递依赖有意对齐，见 `AppAutomation.McpClient.csproj` 注释） |
| 「dist/ 历史大扫除（filter-repo）」 | **不需要**——`git log --all -- dist/` 为空，dist/ 从未入过库 |
| 「Browser 内存无界」 | **已修**——`MaxRequests=1000` + `MaxBodyChars=32000` 封顶已在 `BrowserService.cs:202` |
