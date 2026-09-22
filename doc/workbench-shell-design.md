# 桌面端 Workbench 外壳设计文档（VSCode 式工作台）

> 状态：现行设计（2026-09-22 整理；同日扩充 §11 技术栈 + §12 设计评估）。
> 权威来源：代码（`apps/Desktop/Acme.Desktop.Workbench/` 全部源码 + 两租户 `*Workspace.cs` + `WorkbenchWindow.axaml(.cs)`）+ CLAUDE.md「Desktop architecture」「Workbench shell + theme system」节。本文档是设计综述，代码以仓库为准；**NuGet 版本唯一权威 = 根 `Directory.Packages.props`（CPM）**。

---

## 1. 这是什么设计（一句话定位）

**VSCode 式固定分区工作台（fixed-region workbench），由声明式数据驱动布局树构建——不是自由拖拽 dock。**

| 对比项 | 自由拖拽 dock（VSCode 面板组拖拽 / DockPanelSuite 类库） | **本设计** |
|---|---|---|
| 面板位置 | 运行时任意拖拽、重停靠、浮动 | **分区固定**：左栏/主区/底部/右栏位置不变，只有宽高可拖 |
| 布局定义 | 运行时状态 | **声明式 `LayoutNode` 树**（代码里一行 `LayoutSplit(...)` 写死，JSON 可序列化） |
| 可拖的东西 | 面板本身 | 只有 **GridSplitter**（分界条拖宽高，位置持久化） |
| 面板显隐 | dock/undock/float | **折叠**（行/列宽 0 + toggle）+ **tab-coupled**（切特定页自动收起） |
| 实现成本 | 需 dock 框架（Avalonia 生态无成熟项） | 纯 Grid/GridSplitter + ~600 行布局引擎 |

分区解剖完全对标 VSCode：活动栏（nav rail）→ 侧边栏（左栏树）→ 编辑器区（主面板 tab 页）→ 面板（底部终端）→ 辅助侧栏（右栏 AI 聊天）。但每个分区的内容由**每租户的 `WorkspaceDefinition` 数据声明**，共享 shell 引擎零租户知识。

---

## 2. 窗口解剖（WorkbenchWindow + WorkspaceView 两层）

```
┌─────────────────────────────────────────────────────────────────────┐
│ 自绘标题栏（borderless，全内置主题常驻）：[交通灯Win三键]  Aether  [◧][▤][▣] │
│   ◧=主侧栏 toggle  ▤=终端面板 toggle  ▣=AI 右栏 toggle（VSCode 式标题栏按钮）│
├──────┬───────────────────────────────────────────────────┬──────────┤
│ nav  │ ┌────────────┬──────────────────────────────────┐ │  AI 聊天  │
│ rail │ │ 左栏 240px  │ 主面板（Grid 叠放，nav 是唯一切换器）   │ │ 右栏      │
│      │ │            │ ChatPage│资料库│资料库SCM│文档│SCM    │ │ ChatView │
│ 48px │ │ documents   │ │源代码管理│连接器│自动化│活动│网络     │ │ 420px    │
│ 收起 │ │  →文件树     │ （Stockapp 追加：期货│回测/实盘│股票）   │ │ 可折叠    │
│ 200px│ │ sourcecontrol│                                  │ │ 流式不断  │
│ 展开 │ │  →SCM 面板   │                                  │ │          │
│      │ │ knowledge   │                                  │ │          │
│      │ │  →.md 树    │                                  │ │          │
│      │ │ knowledgescm│                                  │ │          │
│      │ │  →知识库SCM  │                                  │ │          │
│      │ │ （按 tab 切） │                                  │ │          │
│      │ └────────────┴──────────────────────────────────┘ │          │
│      │ ── splitter（可拖高，默认 220px）──────────────────  │          │
│      │ 底部面板：终端 / 网络（内部 Button tab，不销毁切换）     │          │
├──────┴───────────────────────────────────────────────────┴──────────┤
│ 无状态栏。账号按钮在 nav rail 底部（设置/Trace/重置布局/桌宠/主题/登录）      │
│ Toast 右下角悬浮层；DreamLayer（aurora 动画）在最底层                      │
└─────────────────────────────────────────────────────────────────────┘
```

两层分工：

- **`WorkbenchWindow`**（`Acme.Desktop.Workbench/WorkbenchWindow.axaml`，壳层）：标题栏 + nav rail + 工作台宿主 `ContentControl`（绑 `ActiveWorkspace`）+ Toast 宿主。**窗口不决定布局**——中间一切交给 WorkspaceView。
- **`WorkspaceView`**（`Workspace/WorkspaceView.axaml.cs`，布局引擎）：遍历 `IWorkspace.Layout`（LayoutNode 树）**运行时构建视觉树**。换布局 = 换 Workspace，窗口壳不动。

---

## 3. 数据模型（纯数据、可序列化）

`Abstractions/` 下全部是 record：

### WorkspaceDefinition

```csharp
record WorkspaceDefinition(string Id, string Title,
    IReadOnlyList<PanelDescriptor> Panels,   // panel 声明清单
    LayoutNode Layout);                       // 布局树根
record PanelDescriptor(string RegionId, string Key, string Title, string Icon);
```

`RegionId`（布局树里的槽位名）与 `Key`（`IViewRegistry` 解析视图的注册键）概念分离——经 `IRegionManager.AddToRegion` 注入的 panel，Key 决定渲染什么、RegionId 由注入方选定宿主。通常二者同值。

### LayoutNode 树（三种节点，`[JsonPolymorphic("$kind")]`）

| 节点 | 语义 | 运行时构建为 |
|---|---|---|
| `LayoutSplit(Orientation, Children, Pixels?)` | 子节点按方向分割 | `Grid`（子列 + GridSplitter 交替排列）；`Pixels[i]>0` 固定像素，`null`/`<=0` = Star 弹性 |
| `LayoutRegion(RegionId)` | 单 panel 叶子 | `ContentControl`（经 `IViewRegistry` 解析视图），向 `IRegionManager` 登记命名 region（插件注入入口） |
| `LayoutTabbed(RegionIds, SelectedIndex)` | 多 panel「tab」 | **Grid 叠放全部 panel 视图、仅选中项 `IsVisible`**——无 Tab 头，侧栏 nav 是唯一切换入口（视图一次创建常驻，切换不重建） |

> 「tab」是**无头**的——无 TabControl 头，侧栏 nav rail 是唯一切换入口（§5）；视图一次创建常驻，切换只切 `IsVisible` 不重建（`WorkspaceView.BuildTabbed`）。

### 两租户布局树（完全同构，Stockapp 只多三个 tab）

```csharp
// QifuWorkspace.cs / StockappWorkspace.cs（Stockapp 的 RegionIds 追加 Ctp/Backtest/StockTrade）
new LayoutSplit(Horizontal,
    children: [
        new LayoutRegion(LeftTree),                                    // 左栏 240px 固定
        new LayoutSplit(Vertical,                                      // 中区上下分
            children: [
                new LayoutTabbed([ChatPage, Knowledge, KnowledgeScm,   // 主面板（Star 弹性）
                    Documents, SourceControl, Experts, Automation, Activity, Browser]),
                new LayoutRegion(Terminal)],                           // 底部 220px 固定
            pixels: [0, 220]),
        new LayoutRegion(Chat)],                                       // 右栏 420px 固定
    pixels: [240, 0, 420])
```

---

## 4. 构建管线（definition → 屏幕上的像素）

```
AddWorkbench(def)                    注册 def 单例 + IViewRegistry/IRegionManager/IWorkspaceManager
  → WorkbenchShellViewModel.InitializeAsync
      ① LoadThemesAsync + SelectThemeCoreAsync       窗口打开前已应用主题
      ② 恢复 ui.sidebarExpanded
      ③ RestoreLayoutAsync(def)                      合并持久化布局（§6）
      ④ _workspaceManager.ActivateAsync(def)
            → Panels.Select(d => PanelViewModel(d, _viewRegistry.CreateViewModel(d.Key)))
            → new WorkspaceViewModel(...) + InitializeAsync → Active = ws + 事件
  → WorkbenchWindow ContentControl 绑 ActiveWorkspace → WorkspaceView
      ⑤ BuildLayout(LayoutNode) 递归三节点 → Grid/GridSplitter/叠放 Grid
      ⑥ BuildRegion: RegisterRegion（仅登记宿主）→ AddToRegion 设初始内容
         （内容设置集中在 AddToRegion，调换顺序不会抹掉已设视图——防 chat 空白复发）
      ⑦ 订阅三个面板 service 显隐事件 + 解析四个 tab 索引 + 首次 ApplyXxxVisibility
  → base.InitializeAsync（登录态/业务 VM/Chat/调度器）   ← 工作台先上屏再填数据，感知更快
```

**视图注册**：`WorkbenchViewRegistry`（共享）注册 12 个面板视图（Chat/ChatPage/Documents/LeftTree/SourceControl/Terminal=BottomPanelView/Activity/Knowledge/KnowledgeScm/Browser/Experts/Automation），VM 工厂全部经 `MainViewModelBase` 单例解析。**租户注入缝** `IViewRegistryContributor`：Stockapp 的 `StockappViewContributor` 注册 `Ctp→FuturesView`/`Backtest→TradingView`/`StockTrade→StockTradingView`——Workbench 库不依赖租户程序集，租户无 contributor 时共享 12 视图即全量（Qifu）。

**`PanelKeys` 单一真相源**：workspace 定义、view registry、租户 contributor 三处必须用同一 key 串——散落字面量会因拼写不一致导致 panel **静默不渲染**（`CreateView` 返回 null → ContentControl 空）。

---

## 5. 导航 rail（数据驱动派生）

nav rail 的项**不是手写的**——`WorkbenchShellViewModel.RebuildNav` 从活动工作台的**首个 `LayoutTabbed` 节点**派生（`LayoutNode.FindFirstTabbed`）：

- 每个 RegionId → 找 `PanelDescriptor` → `NavItem{Key, Icon, Title, Command=SwitchContentCommand}`。
- `SwitchContent(key)`：key → 首个 tabbed 的索引 → `ActiveWorkspace.SelectedTabIndex = i`（tabbed 的 Grid 叠放切 `IsVisible`；`UpdateNavCurrent` 高亮当前项）。
- **LeftTree/Terminal/Chat 是 LayoutRegion 不进 tabbed → 不进 nav**——左栏按当前 tab 条件显隐（§7），终端是底部常驻，聊天是右栏。
- 图标：`PanelDescriptor.Icon` 是 key → `IconLoader` 解析 codicon path data，未命中回退原串。

侧栏 rail 本体：收起 48px 仅图标 / 展开 200px 图标+文字（`Classes.expanded` 绑 `IsSidebarExpanded`，SharedTheme 切宽），持久化 `ui.sidebarExpanded` 默认收起。底部账号按钮带 MenuFlyout（设置/Trace/重置布局/桌宠/主题列表/登录）。

---

## 6. 布局持久化（drag-writeback + 启动恢复 + 重置）

**写**（`WorkspaceView`）：

```
拖动 GridSplitter → DragCompleted → SyncExpandedSizes()      回写三面板记忆字段（终端行高/左栏宽/右栏宽）
窗口 Closing → OnHostClosing → CollectCurrentLayout()
  → Transform(layout)：LayoutSplit → Pixels = ReadPixels(...)（读子控件 Bounds 实际像素；Star 列保持 Star 不转 Pixel）
                     LayoutTabbed → SelectedIndex = 当前选中
  → JsonSerializer.Serialize → IPreferencesStore["workbench.layout"]
    （Task.Run().Wait() 阻塞写——SetAsync 真异步，UI 线程直接 .Wait 会死锁）
```

**读**（`WorkbenchShellViewModel.RestoreLayoutAsync`）：

- 反序列化失败 / json 空 → 回退默认（不崩）。
- **`CoversSameRegions` 有序校验**：持久化树与代码默认树的 region id 序列**按遍历顺序**逐一相等才采用——面板增删或 tab 顺序变更都判不等 → 回退新默认（免被旧顺序覆盖）。这是「改布局不 bump pref key」的机制。
- 恢复必须在 `ActivateAsync` **之前**合并进 definition（`IWorkspace.Layout` 只读，建后不可改）。

**重置**：账号菜单「重置布局」→ 清 pref key → 重新 `ActivateAsync(默认 def)` → 新 WorkspaceView；旧 view 的 `OnHostClosing` 守卫（`_workspace != mgr.Active`）确保关闭时不覆盖重置。

---

## 7. 三面板折叠体系（终端 / 左栏 / 右栏聊天）

三个面板共用同一模式（**service 回路 + 行/列宽 0 折叠 + 视图不销毁**）：

```
标题栏 toggle → ShellVM RelayCommand → IXxxPanelService.SetVisible(bool)
   service：改态 + 持久化（ui.terminalVisible / ui.chatVisible / ui.leftTreeVisible，默认可见）
          + 触发 XxxVisibilityChanged 事件
   → WorkspaceView 订阅（Dispatcher.Post marshal UI 线程）→ ApplyXxxVisibility：
        折叠 = 行/列宽 GridLength(0) + 分界 splitter 隐 + content IsVisible=false
        展开 = 恢复记忆的展开尺寸（含拖动后实际值）+ splitter 显 + content 显
   → ShellVM 订阅同事件回写 IsXxxVisible（toggle 按钮 accent/灰态）
```

三个 service：`ITerminalService`（行式——终端是垂直 split 的底部行）/ `IChatPanelService` / `ILeftTreePanelService`（列式——右栏/左栏是水平 split 的列）。

**关键性质：折叠视图不销毁**——PTY 会话、聊天流式回复、树展开态在折叠/切 tab 期间全部存活（`IsVisible=false` 只是隐藏空间）。这是全 shell 的通用纪律：**BuildTabbed 建全部视图后只切 IsVisible**，任何面板切换/折叠/恢复都不重建。

**tab-coupled 显隐**（`IsPanelHidingTab`）：切到「关闭类」tab（`ChatPage`/`Experts`/`Automation`/`Activity`/`Browser`）时终端、右栏、（有内容时）左栏**视觉自动收起**（让出空间）；切回编辑/数据页且 toggle 开则恢复。toggle 全局态不变、面板后台继续跑。语义 = `有效可见 = toggleOn && !关闭类tab`。

**左栏的条件可见**（比另两个多一层）：左栏叠放 4 个子树（`LeftTreePanelView.SetActiveTree`：documents→文件树 / sourcecontrol→SCM 面板 / knowledge→.md 树 / knowledgescm→知识库 SCM），按当前 tab 切子树；非编辑类 tab 整列宽 0。子 view 各绑 `MainViewModelBase` 上的共享 VM 单例（`Documents`/`SourceControl`/`Knowledge`），**无跨 view 通信**。手动 toggle（`ILeftTreePanelService`，默认开）叠加其上——关则无视 tab 整体折叠。

**两个防坑机制**：

- **`_hiddenSizes` 记忆表**：折叠后子控件 `IsVisible=false` → `Bounds=0`，会把持久化像素污染成 0（下次启动面板宽 0）。`ReadPixels` 对隐藏宿主取记忆的展开尺寸兜底（`Dictionary<Control,double>`，显隐时增删）。
- **`SyncExpandedSizes`**：拖动只改 Grid 列/行宽、记忆字段还是旧值——不回写的话，下次切导航 `ApplyXxxVisibility` 用旧值覆盖 →「切导航就重置回拖前位置」。`splitter.DragCompleted` 统一回写。

---

## 8. 窗口外壳细节（WorkbenchWindow）

- **自绘标题栏常驻**：四个内置主题 JSON（light/dark/dream/dream-dark）**全部 `chrome.borderless: true`**（2026-08-28 `a592739` 起；CLAUDE.md 曾写「light/dark 保留原生」，已同步修正）。`ApplyChrome`：borderless → `WindowDecorations.BorderOnly`（去系统标题栏、保留边框缩放——免丢 resize）；无 chrome 配置的自定义主题 → `Full` 原生 chrome（此时自绘标题栏与三个 toggle 隐藏）。
- 标题栏右侧 = **三个 VSCode 式面板 toggle**（codicon `LayoutSidebarLeft`/`LayoutPanel`/`LayoutSidebarRight`，accent 开/灰关）+ Windows 三键（`win-btn`）；左侧 = macOS 交通灯（红黄绿，hover 显符号——显隐由 SharedTheme 选择器驱动，XAML 不写局部 IsVisible）。
- **无状态栏**：账号/主题/设置都在 nav rail 底部账号按钮的 MenuFlyout；Toast 气泡右下角悬浮（`ToastHost` 叠放层，不占布局不拦交互）。
- **层次**：Layer 0 DreamLayer（皮肤 `animated: true` 的 aurora 漂移光斑，`IsHitTestVisible=False`）→ Layer 1 shell（DockPanel：标题栏 Top + nav rail Left + 工作台 Fill）→ Layer 2 ToastHost。
- **桌宠**：主窗 X 关闭被拦为「最小化到桌宠」（独立透明 Topmost 悬浮窗，`PetWindow`），非 shell 布局的一部分。
- 子窗口（设置/Trace/对话框）统一继承 `ThemedWindow` 基类，经 `DesktopApp.Services` 静态定位器取当前主题 overlay——overlay 落在 WorkbenchWindow 上，子窗口需各自应用。

---

## 9. 主题系统（与布局正交的另一维度）

简要（详见 CLAUDE.md「Workbench shell + theme system」）：

- **JSON token overlay**：主题 = JSON 文件（颜色/圆角/字体 token），`ThemeLoader.Apply` 反序列化为 ResourceDictionary **叠加**在 `WorkbenchTheme.axaml` 基线之上；`variant` 字段驱动原生控件深浅（`Application.RequestedThemeVariant`）。
- 选中链：账号菜单「主题」→ `SelectThemeCoreAsync` → 设 `CustomThemeJson` → 窗口订阅 `ThemeChanged` → Apply。持久化 `ui.theme`，启动恢复在窗口 Show 之前（开门即已换肤）。
- **皮肤包（Level-2）**：JSON 上加 4 个 opt-in 字段——`gradients`（token 渐变覆盖）/ `animated`（aurora 光斑层）/ `styles`（每肤 `.axaml` 样式包）/ `chrome`（无边框自绘标题栏）。全 null/false = 纯 Level-1 主题。
- **主题目录**：内置 4 个（Assets/*.json，用户同名文件可覆盖）+ `<tenantDir>/themes/*.json` 用户主题（启动扫描）。

---

## 10. 设计决策（为什么这样搞）

1. **不做自由拖拽 dock**：Avalonia 生态无成熟 dock 库；自研停靠系统成本大，而本产品的面板集合是**已知且稳定**的（12 共享 + 3 租户），固定分区 + 可拖宽高已覆盖全部需求。数据驱动树保留了「改布局只改一行声明」的扩展性，未来 AI 生成/插件产出 `WorkspaceDefinition` 也是现成的（纯数据 JSON）。
2. **tab 无头、nav rail 即切换器**：VSCode 的 activity bar + 无头 tab 合并成单一 nav rail——省一层 UI，导航即 tab；视图常驻切换不重建保状态（流式回复/PTY 不中断的前提）。
3. **「先壳后数据」启动序**：主题 → 工作台激活（纯布局+nav）→ 才是登录态/业务 VM/Chat——窗口 Show 后内容区立即可见，不空等一堆异步。
4. **视图常驻不销毁是全壳纪律**：BuildTabbed/BottomPanelView/折叠全部走 `IsVisible` 切换——终端 PTY、聊天流式、浏览器 Playwright 会话都依赖「隐藏 ≠ 销毁」。
5. **`Record` 树 + JSON 序列化 + 有序校验恢复**：布局持久化不用版本号——`CoversSameRegions` 有序比较让「代码改布局」自动作废旧持久化（回退新默认），无需 bump pref key。
6. **共享 shell 零租户知识**：Workbench 库只认 `IViewRegistryContributor` 缝与 `PanelKeys` 常量；租户差异（Stockapp 三个交易页）全部数据化在租户 `*Workspace.cs` + contributor 里。

---

## 11. 技术栈清单（版本唯一权威 = 根 `Directory.Packages.props`）

下表版本取自 CPM 文件 + `obj/project.assets.json` 实解析（传递依赖一并标注）。**注意 CLAUDE.md 个别段落的版本号已漂移**（SkiaSharp 3.119.4→4.151.1、RoyalTerminal 0.5.1→0.5.2、Svg.Skia 3.7.0→5.2.3），以 CPM 为准。

### UI 框架与运行时

| 包 | 版本 | 用在哪 | 选型理由 / 替代方案 |
|---|---|---|---|
| **.NET** | 10（`net10.0`） | 全部桌面项目 | Stockapp Windows 条件双 TFM（`net10.0-windows;net10.0`，FlaUI 需要） |
| **Avalonia** | 12.1.1 | 全部 | .NET 跨平台 XAML UI 的**唯一成熟非 WPF 选择**（MAUI 不支持 Linux/macOS 桌面品质）。12.0.4→12.1.1 的升级动因 = RoyalTerminal 最低版本要求 |
| Avalonia.Skia / HarfBuzzSharp | 12.1.1 / 14.2.0 | 渲染后端 | Skia 渲染 + 文本整形（CJK 关键——中文 UI 必须正确整形） |
| Avalonia.Themes.Fluent | 12.1.1 | 租户 App | Fluent 基线控件样式；深浅切换走 `RequestedThemeVariant`（§9） |
| Avalonia.Desktop / Fonts.Inter | 12.1.1 | 租户宿主 | 桌面平台宿主 + Inter 内置字体 |
| Microsoft.Extensions.DependencyInjection | 10.0.11 | 组合根 | 通用 DI 容器（`AddDesktopCommon`/`AddWorkbench`） |

### MVVM 基础

| 包 | 版本 | 理由 |
|---|---|---|
| **CommunityToolkit.Mvvm** | 8.4.2 | source-generator `ObservableObject`/`[ObservableProperty]`/`[RelayCommand]`——.NET 社区标准 MVVM 库，无争议选择。**Mvvm 层零 Avalonia 引用**（UI 无关，手机端 MAUI 复用同一 VM 库——双轨架构的共享缝） |
| CommunityToolkit.Maui | 14.2.0 | 手机端同族（桌面 Avalonia + 手机 MAUI 双轨，Mvvm 层居中共享） |

### 控件与内容渲染（Avalonia 生态专用件）

| 包 | 版本 | 用途 | 备注 |
|---|---|---|---|
| **AvaloniaEdit** | 12.0.0（传递 ColorDocument/ColorTextBlock 12.0.0-a3） | VSCode 式代码编辑器（语法高亮/折叠/Ctrl+F 搜索/行号） | Avalonia 生态**事实标准**编辑器；YAML 高亮需自定义 .xshd |
| **Markdown.Avalonia.Tight** | 12.0.0-a3 | 聊天/文档 markdown 渲染 | alpha 预发布是生态现状；Tight fork 修表格排版；`SelectableMarkdownViewer` 子类化获得选择复制 |
| **RoyalApps.RoyalTerminal.Avalonia** | 0.5.2 | 集成终端（真实 PTY：Unix forkpty / Win ConPTY） | **全生态唯一可用**的 Avalonia PTY 控件（WriteOutput/SendInput/DataReceived），为此整体升级 Avalonia 12.1.1 |
| ProDataGrid | 12.1.0.4 | CSV 表格预览 | MIT、drop-in 官方 DataGrid 替代（绕反射绑定坑） |
| IconPacks.Avalonia.Codicons | 2.0.0 | 全 app 图标（VSCode codicon 填充 glyph） | 视觉语言直接对标 VSCode |
| **Mermaider** | 0.12.2 | mermaid → SVG（纯托管，无 JS 运行时） | `MermaidSvgRenderer` 再对 SVG 后处理（节点/边 class 重定位拉直） |
| Svg.Skia / Svg.Controls.Skia.Avalonia | 5.2.3 / 12.0.0.17 | SVG 光栅显示（mermaid 图、图标） | |
| **SkiaSharp + SkiaSharp.Skottie** | 4.151.1 | 桌宠 Lottie 矢量动画（离屏 SKBitmap→WriteableBitmap） | libSkia 内置 skottie 符号，零新增原生库 |

### 引擎类功能底座

| 包 | 版本 | 用途 |
|---|---|---|
| **LibGit2Sharp** | 0.31.0 | SCM 页全部 git 操作（status/diff/commit/push）——零 git 安装依赖，libgit2 原生绑定 |
| **Microsoft.Playwright** | 1.61.0 | 网络页：headed Chromium 持久会话（cookie 存盘）+ DevTools 风格请求捕获 |
| **Microsoft.CodeAnalysis.CSharp.Workspaces / Workspaces.MSBuild** | 4.13.0 | `edit_symbol` 语义编辑（MSBuildWorkspace 跨项目符号解析） |

**生态成熟度总评**：Avalonia 专用控件库普遍处于 0.x/alpha（RoyalTerminal 0.5.2、Markdown alpha、Mermaider 0.12）——这是选 Avalonia 的固有代价，换来 .NET 10 同代 + 三桌面平台 + 与 MAUI 手机端共享 Mvvm 层。升级破坏风险高于 WPF/WinUI 生态，版本钉死在 CPM 便于统一升降。

---

## 12. 设计评估（合理吗 / 主流吗）

### 12.1 布局形态：完全主流

**fixed-region workbench 是桌面 IDE/专业工具的主流形态**，Aether 五分区与 VSCode 一一对应：

| Aether | VSCode（及 Cursor/Windsurf 等 fork） | 状态 |
|---|---|---|
| nav rail（48↔200px 收展） | Activity Bar | 同构 |
| 左栏树（文件/SCM/知识树按 tab 切） | Explorer/SourceControl 侧边栏 | 同构（合并为单栏 tab 切换是简化变体） |
| 主面板（无头 tab） | Editor tab 组 | 同构（无头、nav 即切换器） |
| 底部面板（终端/网络） | Panel（Terminal/Output） | 同构 |
| 右栏聊天 | Auxiliary Side Bar（chat panel） | 同构——对 AI 桌面产品即 ChatGPT desktop/Cursor 的 chat 面板模式 |

聊天**双视图**（全屏 ChatPage 居中输入 + 可折叠右栏紧凑视图，共享同一 VM）对齐 ChatGPT 桌面端「首页对话 ↔ 侧边随行」的双形态。**不做的部分**：VSCode 的面板拖拽重排/浮动（dock）——Avalonia 无成熟 dock 库、自研成本高，且面板集合已知稳定；固定分区在 app 形态产品（ChatGPT desktop、Linear）中同样普遍。**这是合理裁剪而非缺陷**。

### 12.2 实现路径：主流思想 + 自研轻量化

形态主流，但实现不是现成框架——三条主流路线对比：

| 路线 | 代表 | 为何不选 |
|---|---|---|
| 硬编码 XAML | 小型 app | 布局写死，租户差异需分叉窗口 |
| dock 框架 | WPF: DockPanelSuite/AvalonDock | **Avalonia 无成熟等价物** |
| region 导航框架 | Prism regions | 重框架 + 未跟进 Avalonia 12 |

Aether 的 `LayoutNode` 树 + `IRegionManager`（RegisterRegion/AddToRegion）实质是 **Prism region 模式的极简自研版**（同名概念直接对应）；布局持久化（drag-writeback → JSON state → 有序校验恢复）对标 **VSCode 自身的布局状态 JSON 序列化**。声明式树的额外收益：租户差异数据化 + 未来插件/AI 产出 `WorkspaceDefinition` 即纯数据。代价：**~600 行 view code-behind 布局引擎**（构建/折叠/持久化）——XAML DataTemplate 表达不了运行时树，这是声明式布局的必然成本，不是失误。

### 12.3 主题系统：方向主流，细节是 VSCode 式变体

**token 化（design tokens）是行业共识**——语义 token + 主题文件换值的思路与 CSS custom properties / Material tokens / Fluent theme resources 同源。实现细节的直接对标物是 **VSCode `workbench.colorCustomizations`**：JSON 换 token 值、用户免重编自定义（`<tenantDir>/themes/*.json`）。三层正交（axaml 基线 + JSON overlay + `ThemeVariant` 原生控件深浅）比多数单一机制主题系统更完整；皮肤包 Level-2（渐变/aurora/自绘 chrome）是 showcase 需求（对标 Codex-Dream-Skin-Themes 炫皮肤生态），非主流但有明确对标物。

### 12.4 已知妥协与风险（诚实清单）

1. **`CompiledBindings=false` 全仓反射绑定**：非 Avalonia 推荐（编译绑定是默认最佳实践）——运行时查找开销 + VM 拆分改名**静默失效**（需 grep 核对绑定名的专门纪律）。历史遗留选择，成本已显性化但未消除。
2. **布局引擎在 code-behind**：MVVM 纯净度让位于运行时树构建的现实需要；shell VM 保持 UI 无关（`GridLength` 不进 VM），代价是折叠/持久化逻辑全在 view 侧。
3. **Avalonia 12.1.1 前沿 API**：`WindowDecorations`/无 `StateChanged`/无 `DoubleTappedEventArgs`——12.x chrome API 仍在演进，先行者踩坑成本（Gotcha ⑦ 已记录）。
4. **生态早期版本**：RoyalTerminal 0.5.2 / Markdown alpha / Mermaider 0.12——升级破坏风险高于主流库（钉 CPM 缓解）。
5. **双 UI 框架双轨**（桌面 Avalonia + 手机 MAUI）：Mvvm 层共享是收益，但聊天等复杂视图写了两份（desktop axaml + mobile xaml）——已知取舍。

### 12.5 总评

| 维度 | 评价 |
|---|---|
| 布局形态 | ✅ 主流（VSCode 谱系五分区，逐项同构） |
| 布局实现 | ⚠️ 自研轻量声明式引擎——Prism region 思想 + VSCode 布局持久化思想，合理但非现成框架（Avalonia 生态缺位下的正确路线） |
| 主题 | ✅ 主流方向（token 化）+ VSCode 式 JSON overlay 细节，三层正交完整 |
| 技术栈 | ✅ Avalonia 为 .NET 跨平台唯一成熟选；⚠️ 专用控件生态 0.x/alpha 偏早期 |
| MVVM | ✅ CommunityToolkit 标准做法；⚠️ 反射绑定 + 布局 code-behind 两处显性妥协 |
| **总体** | **形态对齐主流专业工具；实现走「主流思想 + 自研轻量化」路线，无架构性错误；主要风险在生态早期版本与反射绑定的长期维护成本** |

---

## 13. Gotchas（实踩过的坑）

1. **`LayoutSplit` 是 record（值相等）**——`_splitState` 字典必须用 `ReferenceEqualityComparer` 键控，否则两个结构相同的 split 撞键。
2. **隐藏即 Bounds=0**——折叠/切 tab 隐藏后持久化会把像素写成 0；`_hiddenSizes` 记忆表 + `ReadPixels` 兜底是必须的（§7）。
3. **`SetAsync` 真异步**——UI 线程（窗口 Closing）持久化必须 `Task.Run(...).Wait()`，直接 `.Wait()` 会 SynchronizationContext 死锁；`GetAsync` 同步完成（`Task.FromResult`）可安全 `.GetAwaiter().GetResult()`。
4. **dock Quit 时容器先释放**——`OnHostClosing` 里裸 `GetService` 抛 `ObjectDisposedException` → SIGABRT 崩溃；走 `DesktopApp.TryGetService` 容错解析，容器已释放则各步静默跳过（持久化本就 best-effort）。
5. **lefttree 与 chat 的 splitter 藏向相反**——左栏恒为首 child（藏其**后**边界 splitter `i*2+1`），聊天恒为末 child（藏其**前** splitter `i*2-1`）；写反则藏错条。
6. **`SelectedTabIndex` 订阅要 owner 守卫**——布局重建/切换工作台时旧 workspace 的通知不得影响新视图（`BuildTabbed` 闭包捕获 owner 比对）。
7. **Avalonia 12.1.1 chrome API**：用 `WindowDecorations` 枚举（`None`/`BorderOnly`/`Full`），`ExtendClientAreaChromeHints` 不存在、`SystemDecorations` 已废弃；无 `StateChanged` 事件——捕获最大化↔还原须订阅通用 `AvaloniaObject.PropertyChanged` 过滤 `WindowStateProperty`；双击复用 `TappedEventArgs`（无 `DoubleTappedEventArgs` 类型）。
8. **`MenuItem.Flyout`** Avalonia 12 没有——主题子菜单用 `MenuItem.ItemsSource` + `ItemTemplate` + `Click` code-behind。
9. **布局恢复只认有序同序列**：改 tab 顺序 = 回退默认（有意为之，防止旧顺序覆盖新默认）；若只想改像素不想回退，须保持 region 序列不变。
10. **tab 是无头的**：`LayoutTabbed` 实现为 Grid 叠放 + IsVisible 切换（无 TabControl 头——nav rail 是唯一切换入口，视图常驻切换不重建）。`LayoutNode.cs` 注释曾写「TabControl 平铺」、CLAUDE.md 曾写「light/dark 保留原生 chrome」/toggle 在侧栏底部——三处 2026-09-22 已同步修正。
