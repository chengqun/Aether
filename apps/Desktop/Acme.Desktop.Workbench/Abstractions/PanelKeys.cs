namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// Panel key 常量目录——view-key（<see cref="IViewRegistry"/> 解析视图）+ region id（<see cref="LayoutRegion"/>/<see cref="LayoutTabbed"/> 引用槽位）
/// 的单一真相源。workspace 定义、view registry、租户 contributor 三处必须用同一 key 串，散落字面量会因拼写不一致导致 panel 静默不渲染
/// （<c>CreateView</c> 返回 null → ContentControl 空）。全部经此引用，编译期防拼写错误、重命名一处生效。
/// </summary>
/// <remarks>
/// workspace 定义里 <see cref="PanelDescriptor"/> 的 RegionId 与 Key 通常相同（panel 既是某 region 的初始内容、又按 key 解析视图）；
/// 但概念上二者不同——经 <see cref="IRegionManager.AddToRegion"/> 注入的 panel，其 Key 决定渲染什么、RegionId 由注入方选定的 region 名定位宿主。
/// </remarks>
public static class PanelKeys
{
    /// <summary>聊天面板（共享 ChatView，右 region 紧凑视图）。</summary>
    public const string Chat = "chat";

    /// <summary>全屏 AI 聊天页（共享 ChatPageView，tabbed nav 首项——app 启动默认页）。
    /// 与 <see cref="Chat"/>（右栏紧凑 ChatView）共享同一 ChatViewModel，仅头部/输入布局不同（全屏居中 ChatGPT 式输入）。</summary>
    public const string ChatPage = "chatpage";

    /// <summary>笔记面板（NotesView + AvaloniaEdit + form 节点两栏）。</summary>
    public const string Notes = "notes";

    /// <summary>打开文档面板（DocumentsEditorView，多标签 AvaloniaEdit 编辑器，打开磁盘文件）。</summary>
    public const string Documents = "documents";

    /// <summary>shell 级左栏面板（LeftTreePanelView，VSCode 式目录栏占满高度——终端只压编辑器区不压树）。
    /// 是 LayoutRegion 不进 LayoutTabbed → 不进 nav rail；叠放文件树 + 笔记树，按 SelectedTabIndex 切显隐：
    /// documents tab→文件树 / notes tab→笔记树 / 其它 tab→列宽 0 隐藏（WorkspaceView.ApplyLeftTreeVisibility 驱动）。
    /// DataContext = MainViewModelBase；子 view 各绑 Documents/Notes 共享 VM 单例。</summary>
    public const string LeftTree = "lefttree";

    /// <summary>源代码管理面板（SourceControlView，git status/diff/stage/commit/discard）。</summary>
    public const string SourceControl = "sourcecontrol";

    /// <summary>集成终端面板（TerminalView，多标签真实 PTY；agent run_in_terminal 工具的目标）。</summary>
    public const string Terminal = "terminal";

    /// <summary>浏览器网络面板（BrowserView，Playwright 持久会话 + 实时记录 HTTP 请求；agent browser_open/get_cookies 工具的目标）。
    /// 与终端共用底部 tabbed region（LayoutTabbed([Terminal, Browser]) 切换），不进 nav rail。</summary>
    public const string Browser = "browser";

    /// <summary>活动热力图面板（ActivityView，GitHub 式贡献图：Agent 执行 + Git 提交按日聚合，点格看当天明细）。</summary>
    public const string Activity = "activity";

    /// <summary>知识对象面板（KnowledgeObjectsView，文档版 <c>.ok</c> 知识对象：左 .ok 树 + 右表单/源码两栏）。
    /// 根目录=独立知识库目录（<see cref="Acme.Desktop.Mvvm.IKnowledgeDirectoryProvider"/>，跨项目复用，与文档页项目目录解耦）。
    /// note 版知识对象仍在笔记树（<see cref="Notes"/>），本面板只承载文档版。</summary>
    public const string Knowledge = "knowledge";

    /// <summary>知识对象源代码管理面板（独立 nav tab，与 <see cref="Knowledge"/> 编辑页并列——两 tab 各管各的，零状态串扰）。
    /// 左=SCM 变更列表（SourceControlPanelView）+ 右=diff（SourceControlView），DataContext 绑
    /// <c>Knowledge.SourceControl</c>（知识库目录的 git——与项目 SCM 页 <see cref="SourceControl"/> 分别走不同目录 provider）。</summary>
    public const string KnowledgeScm = "knowledgescm";

    /// <summary>专家面板（ExpertsView，6 tab）。</summary>
    public const string Experts = "experts";

    /// <summary>自动化面板（AutomationView，定时任务）。</summary>
    public const string Automation = "automation";

    /// <summary>Stockapp 期货面板（FuturesView，CTP 行情/交易）。</summary>
    public const string Ctp = "ctp";

    /// <summary>Stockapp 回测/实盘面板（TradingView，Lean 引擎）。</summary>
    public const string Backtest = "backtest";

    /// <summary>Stockapp 股票交易面板（StockTradingView，FlaUI/MCP 路由）。</summary>
    public const string StockTrade = "stocktrade";
}
