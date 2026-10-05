using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench;

/// <summary>
/// 工作台视图：遍历 <see cref="IWorkspace.Layout"/>（LayoutNode 树）运行时构建视觉树——
/// LayoutSplit → Grid + GridSplitter；LayoutTabbed → TabControl；LayoutRegion → ContentControl（经 IViewRegistry 解析视图）。
/// 每个 LayoutRegion 叶子向 <see cref="IRegionManager"/> 登记命名 region（插件面板注入入口）。
/// MainWindow 不决定布局——布局由 Workspace 决定。
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>布局持久化键（每租户独立 preferences.json，无需带 workspace id）。</summary>
    private const string LayoutPrefKey = "workbench.layout";

    // ── Proportional split 的最小尺寸钳制（防窗口缩到最小时星列被压死；MinSizeFor 表与折叠路径共用）──
    private const double LeftTreeMinWidth = 180;
    private const double ChatMinWidth = 320;
    private const double TerminalMinHeight = 120;
    private const double MainMinWidth = 300;
    private const double MainMinHeight = 200;

    private IViewRegistry? _viewRegistry;
    private IRegionManager? _regionManager;
    private IWorkspace? _workspace;

    /// <summary>正在构建的布局树根（收集时遍历它回写拖动后的实际尺寸）。</summary>
    private LayoutNode? _rootLayout;

    /// <summary>每个 LayoutSplit 节点 → 它的 Grid + 子控件列表（读子控件 Bounds.Width/Height 取拖动后实际像素宽）。
    /// record 默认按值相等会撞键，必须用引用相等（ReferenceEqualityComparer）。</summary>
    private readonly Dictionary<LayoutNode, (Grid Grid, List<Control> Children)> _splitState
        = new(ReferenceEqualityComparer.Instance);

    // ── 底部终端面板折叠（方案 B toggle）：记 terminal region 的宿主 + 父 split Grid + 行位置，折叠时改行高 ──
    private ContentControl? _terminalHost;           // terminal region 的 ContentControl（BuildRegion 时记）
    private Grid? _terminalSplitGrid;                // 含 terminal 的 split Grid
    private int _terminalRow = -1;                   // terminal 在 grid 的 child row（= i*2）
    private int _terminalSplitterRow = -1;           // terminal 前的 GridSplitter row（= i*2-1）
    private GridLength _terminalExpandedHeight = new(220, GridUnitType.Pixel); // 展开行高（折叠前记实际值，含拖动后；比例 split 为星权重长度）
    private bool _terminalProportional;             // 终端行所在 split 是否比例分摊——折叠/展开的 MinHeight 钳制解除/恢复判据
    private ITerminalService? _terminalService;

    // ── 侧栏 shell 级左栏条件可见（VSCode 式）：记 lefttree region 宿主 + 父 split Grid + 列位置，
    //    叠放文件树+SCM 面板+.md 树，documents/sourcecontrol/knowledge tab 可见并切对应树，其余 tab 隐藏列宽 0 ──
    private ContentControl? _leftTreeHost;           // lefttree region 的 ContentControl（BuildRegion 时记）
    private Grid? _leftTreeSplitGrid;                // 含 lefttree 的外层水平 split Grid
    private int _leftTreeCol = -1;                   // lefttree 在 grid 的 child col（= i*2）
    private int _leftTreeSplitterCol = -1;           // lefttree 前的 GridSplitter col（= i*2-1）
    private GridLength _leftTreeExpandedWidth = new(240, GridUnitType.Pixel); // 展开列宽（隐藏前记实际值，含拖动后；比例 split 为星权重长度）
    private bool _leftTreeProportional;              // 左栏列所在 split 是否比例分摊——同终端（MinWidth 钳制）
    private ILeftTreePanelService? _leftTreeService;  // 主侧栏手动 toggle（镜像 chat service；默认开）
    private int _documentsTabIndex = -1;             // documents 在 LayoutTabbed.RegionIds 中的索引（-1 = 无 → 侧栏不显文件树）
    private int _sourceControlTabIndex = -1;         // sourcecontrol 在 LayoutTabbed.RegionIds 中的索引（-1 = 无 → 侧栏不显 SCM 面板）
    private int _knowledgeTabIndex = -1;             // knowledge 在 LayoutTabbed.RegionIds 中的索引（-1 = 无 → 侧栏不显 .md 树）
    private int _knowledgeScmTabIndex = -1;          // knowledgescm 在 LayoutTabbed.RegionIds 中的索引（-1 = 无 → 侧栏不显知识库 SCM 面板）
    private INotifyPropertyChanged? _workspaceNpc;   // 订阅 SelectedTabIndex 变化切侧栏显隐（重建时解订阅旧 workspace）

    // ── 右侧 AI 聊天面板折叠（VSCode 式可折叠侧栏，对齐 ChatGPT 桌面端）：记 chat region 的宿主 + 父 split Grid + 列位置，折叠时改列宽 ──
    // 列式 twin（镜像 LeftTree，非终端行式——聊天是顶层水平 split 的右 region）。折叠时聊天视图不销毁（IsVisible=false），流式不中断。
    private ContentControl? _chatHost;               // chat region 的 ContentControl（BuildRegion 时记）
    private Grid? _chatSplitGrid;                    // 含 chat 的 split Grid
    private int _chatCol = -1;                        // chat 在 grid 的 child col（= i*2）
    private int _chatSplitterCol = -1;                // chat 前的 GridSplitter col（= i*2-1）
    private GridLength _chatExpandedWidth = new(420, GridUnitType.Pixel); // 展开列宽（折叠前记实际值，含拖动后；比例 split 为星权重长度）
    private bool _chatProportional;                 // 聊天列所在 split 是否比例分摊——同终端（MinWidth 钳制）
    private IChatPanelService? _chatService;

    /// <summary>隐藏宿主尺寸记忆表：折叠/切 tab 隐藏后子控件 Bounds=0，ReadPixels 用此记忆的展开尺寸替换，防持久化 0。
    /// key=隐藏的 ContentControl（terminal/lefttree/chat host），value=隐藏前的<b>实际像素</b>（Bounds）——
    /// 像素 split 当回退宽；比例 split 的 ReadProportionalWeights 当伪实际算权重（星权重值不是像素，故记 Bounds 而非 GridLength.Value）。显隐时增删。</summary>
    private readonly Dictionary<Control, double> _hiddenSizes = new();

    private bool _persistWired;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += WireHostClosing;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _workspace = DataContext as IWorkspace;
        _splitState.Clear();
        _terminalHost = null;
        _terminalSplitGrid = null;
        _terminalRow = -1;
        _terminalSplitterRow = -1;
        _leftTreeHost = null;
        _leftTreeSplitGrid = null;
        _leftTreeCol = -1;
        _leftTreeSplitterCol = -1;
        _chatHost = null;
        _chatSplitGrid = null;
        _chatCol = -1;
        _chatSplitterCol = -1;
        _terminalProportional = false;
        _leftTreeProportional = false;
        _chatProportional = false;
        _hiddenSizes.Clear();
        // 解订阅旧 workspace 的 SelectedTabIndex 通知（重建/切换工作台时）。
        if (_workspaceNpc is not null)
        {
            _workspaceNpc.PropertyChanged -= OnWorkspaceSelectedTabChanged;
            _workspaceNpc = null;
        }
        if (_workspace is null)
        {
            _rootLayout = null;
            Content = null;
            return;
        }

        EnsureServices();
        _rootLayout = _workspace.Layout;
        Content = _rootLayout is not null ? BuildLayout(_rootLayout, _workspace) : null;

        // 订阅底部终端面板显隐（方案 B toggle）：折叠/展开 terminal region 的 Grid 行高（PTY 不中断，仅隐藏空间）。
        _terminalService ??= DesktopApp.Services?.GetService<ITerminalService>();
        if (_terminalService is not null)
        {
            _terminalService.PanelVisibilityChanged -= OnTerminalPanelVisibilityChanged; // 防重复订阅
            _terminalService.PanelVisibilityChanged += OnTerminalPanelVisibilityChanged;
            ApplyTerminalVisibility(_terminalService.IsPanelVisible);
        }

        // 订阅右侧 AI 聊天面板显隐（VSCode 式可折叠侧栏）：折叠/展开 chat region 的 Grid 列宽（聊天视图不销毁，流式不中断）。
        // BuildLayout 已跑（上方 Content = BuildLayout），故 _chatSplitGrid/_chatCol 已填充。初次显隐在下方调用。
        _chatService ??= DesktopApp.Services?.GetService<IChatPanelService>();
        if (_chatService is not null)
        {
            _chatService.ChatVisibilityChanged -= OnChatVisibilityChanged; // 防重复订阅
            _chatService.ChatVisibilityChanged += OnChatVisibilityChanged;
        }

        // 订阅主侧栏手动 toggle（VSCode 式可折叠左栏）：折叠/展开 lefttree region 的 Grid 列宽（树视图不销毁，展开状态不丢）。
        _leftTreeService ??= DesktopApp.Services?.GetService<ILeftTreePanelService>();
        if (_leftTreeService is not null)
            _leftTreeService.LeftTreeVisibilityChanged -= OnLeftTreeVisibilityChanged; // 防重复订阅
        if (_leftTreeService is not null)
            _leftTreeService.LeftTreeVisibilityChanged += OnLeftTreeVisibilityChanged;

        // 侧栏 shell 级左栏条件可见（VSCode 式）：documents tab→文件树 / sourcecontrol tab→SCM / knowledge tab→.md 树 / 其余 tab→列宽 0 隐藏。
        var tabbed = LayoutNode.FindFirstTabbed(_workspace.Layout);
        _documentsTabIndex = -1;
        _sourceControlTabIndex = -1;
        _knowledgeTabIndex = -1;
        _knowledgeScmTabIndex = -1;
        if (tabbed is not null)
        {
            for (int i = 0; i < tabbed.RegionIds.Count; i++)
            {
                if (tabbed.RegionIds[i] == PanelKeys.Documents) _documentsTabIndex = i;
                else if (tabbed.RegionIds[i] == PanelKeys.SourceControl) _sourceControlTabIndex = i;
                else if (tabbed.RegionIds[i] == PanelKeys.Knowledge) _knowledgeTabIndex = i;
                else if (tabbed.RegionIds[i] == PanelKeys.KnowledgeScm) _knowledgeScmTabIndex = i;
            }
        }
        if (_workspace is INotifyPropertyChanged pc)
        {
            _workspaceNpc = pc;
            pc.PropertyChanged += OnWorkspaceSelectedTabChanged;
        }
        ApplyLeftTreeVisibility(ResolveLeftTreeKind(_workspace.SelectedTabIndex));
        // 须在 BuildLayout 后调（_chatSplitGrid/_chatCol 已填充）；纯 toggle 驱动，与 tab 无关。
        ApplyChatVisibility();
    }

    private void EnsureServices()
    {
        _viewRegistry ??= DesktopApp.Services?.GetService<IViewRegistry>();
        _regionManager ??= DesktopApp.Services?.GetService<IRegionManager>();
    }

    private Control BuildLayout(LayoutNode node, IWorkspace ws) => node switch
    {
        LayoutRegion r => BuildRegion(r, ws),
        LayoutSplit s => BuildSplit(s, ws),
        LayoutTabbed t => BuildTabbed(t, ws),
        _ => new TextBlock { Text = "未知布局节点：" + node.GetType().Name }
    };

    /// <summary>单 panel 叶子 + 命名 region 登记。插件可经 RegionManager 往此 region 注入 panel。
    /// 内容变更契约：先 RegisterRegion（仅登记宿主、不碰 Content），再 AddToRegion 设初始内容——
    /// 内容设置集中在 AddToRegion，RegisterRegion 无副作用，故调换顺序或误加 Content 清空都不会抹掉已设视图（防 chat 空白 bug 复发）。</summary>
    private Control BuildRegion(LayoutRegion r, IWorkspace ws)
    {
        var cc = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _regionManager?.RegisterRegion(r.RegionId, cc);
        var panel = ws.Panels.FirstOrDefault(p => p.RegionId == r.RegionId);
        if (panel is not null)
            _regionManager?.AddToRegion(r.RegionId, panel); // 初始内容走与插件注入相同的路径
        if (r.RegionId == PanelKeys.Terminal) _terminalHost = cc; // 记终端宿主，折叠时改其父 Grid 行高
        if (r.RegionId == PanelKeys.LeftTree) _leftTreeHost = cc; // 记侧栏宿主，切 tab 时改其父 Grid 列宽 + SetActiveTree
        if (r.RegionId == PanelKeys.Chat) _chatHost = cc; // 记聊天宿主，折叠时改其父 Grid 列宽（聊天视图不销毁，流式不中断）
        return cc;
    }

    /// <summary>分割容器：子列 + 分隔条列交替（child, splitter, child, splitter, ...），相邻子节点之间可拖 GridSplitter。</summary>
    private Control BuildSplit(LayoutSplit s, IWorkspace ws)
    {
        var grid = new Grid();
        bool horiz = s.Orientation == SplitOrientation.Horizontal;
        int n = s.Children.Count;
        if (n == 0) return grid;
        int total = n * 2 - 1; // 子节点 + 分隔条
        for (int i = 0; i < total; i++)
        {
            bool isSplitter = (i % 2 == 1);
            GridLength len;
            if (isSplitter)
                len = new GridLength(6);
            else
            {
                int childIdx = i / 2;
                if (s.Proportional)
                    // 比例分摊（2026-10-05）：Pixels[i] = 星权重（<=0 兜底 1）——子区随窗口缩放按比例同伸缩。
                    // GridSplitter 拖完会把星列改写成像素长度，SyncExpandedSizes → RenormalizeProportionalSplits 转回星。
                    len = new GridLength(
                        s.Pixels is not null && childIdx < s.Pixels.Length && s.Pixels[childIdx] > 0 ? s.Pixels[childIdx] : 1,
                        GridUnitType.Star);
                else
                    // 像素语义照旧：Pixels[i] > 0 → 固定像素（如 chat 420px）；null 或 <= 0 → Star（弹性，如内容区）。
                    len = (s.Pixels is not null && childIdx < s.Pixels.Length && s.Pixels[childIdx] > 0)
                        ? new GridLength(s.Pixels[childIdx], GridUnitType.Pixel)
                        : new GridLength(1, GridUnitType.Star);
            }
            if (horiz) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = len });
            else grid.RowDefinitions.Add(new RowDefinition { Height = len });
            // 比例 split 的星列加最小尺寸钳制——防窗口缩到最小时被压死（像素 split 不加：折叠路径只处理比例列的钳制解除）。
            if (s.Proportional && !isSplitter && MinSizeFor(s.Children[i / 2], horiz) is { } min)
            {
                if (horiz) grid.ColumnDefinitions[^1].MinWidth = min;
                else grid.RowDefinitions[^1].MinHeight = min;
            }
        }
        // 子控件先建好再登记 split 状态——收集时读子控件 Bounds.Width/Height 取拖动后实际像素宽。
        var children = new List<Control>(n);
        for (int i = 0; i < n; i++)
        {
            var child = BuildLayout(s.Children[i], ws);
            children.Add(child);
            // 含 terminal region 的 split：记 Grid + terminal 的行位置，用于折叠/展开底部面板（方案 B toggle）。
            if (ReferenceEquals(child, _terminalHost) && _terminalSplitGrid is null)
            {
                _terminalSplitGrid = grid;
                _terminalRow = i * 2;
                _terminalSplitterRow = i * 2 - 1; // terminal 前的 splitter（terminal 非首 child 时存在）
                _terminalExpandedHeight = grid.RowDefinitions[i * 2].Height;
                _terminalProportional = s.Proportional;
            }
            // 含 lefttree region 的外层水平 split：记 Grid + lefttree 的列位置，用于切 tab 时显隐左栏（VSCode 式）。
            if (ReferenceEquals(child, _leftTreeHost) && _leftTreeSplitGrid is null && horiz)
            {
                _leftTreeSplitGrid = grid;
                _leftTreeCol = i * 2;
                // lefttree 恒为首 child（i=0）——它前面的 splitter 不存在，要藏的是它**后面**的边界 splitter（idx+1 = i*2+1）。
                // 与 chat（末 child，藏其前 splitter i*2-1）方向相反，故此处用 +1。
                _leftTreeSplitterCol = i * 2 + 1;
                _leftTreeExpandedWidth = grid.ColumnDefinitions[i * 2].Width;
                _leftTreeProportional = s.Proportional;
            }
            // 含 chat region 的水平 split：记 Grid + chat 的列位置，用于折叠/展开右栏（VSCode 式可折叠侧栏）。
            // horiz 守卫——聊天是顶层水平 split 直子；若未来嵌进垂直 split 则跳过、ApplyChatVisibility 空守卫早返不崩。
            if (ReferenceEquals(child, _chatHost) && _chatSplitGrid is null && horiz)
            {
                _chatSplitGrid = grid;
                _chatCol = i * 2;
                _chatSplitterCol = i * 2 - 1; // chat 前的 splitter（chat 非首 child 时存在）
                _chatExpandedWidth = grid.ColumnDefinitions[i * 2].Width;
                _chatProportional = s.Proportional;
            }
            int idx = i * 2;
            if (horiz) Grid.SetColumn(child, idx); else Grid.SetRow(child, idx);
            grid.Children.Add(child);
            if (i < n - 1)
            {
                // 分界线 = 常显 hairline 模板（SharedTheme 的 GridSplitter.split-v / split-h：静止 1px SplitterBrush、
                // hover 3px AccentBrush）——方向按 split Orientation 打 class。纯视觉模板，拖拽逻辑在 GridSplitter 控件级。
                var splitter = new GridSplitter
                {
                    ResizeDirection = horiz ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    Focusable = false,
                };
                splitter.Classes.Add(horiz ? "split-v" : "split-h");
                // 拖动结束同步展开尺寸记忆字段——否则拖动只改 Grid 列/行宽、`_xxxExpandedWidth/Height` 还是旧值，
                // 切导航时 ApplyXxxVisibility 用旧值覆盖 → 分栏「重置」回拖动前位置（VSCode 应保持拖动后位置）。
                splitter.DragCompleted += (_, _) => SyncExpandedSizes();
                if (horiz) { splitter.Width = 6; Grid.SetColumn(splitter, idx + 1); }
                else { splitter.Height = 6; Grid.SetRow(splitter, idx + 1); }
                grid.Children.Add(splitter);
            }
        }
        _splitState[s] = (grid, children);
        return grid;
    }

    /// <summary>多 panel 叶子：Grid 叠放所有 panel 视图，仅选中项 IsVisible（侧栏 nav 是唯一切换入口，无 Tab 头）。
    /// 视图一次性创建并常驻（切换不重建，状态保留——同 TabControl 行为）。SelectedTabIndex 变化时切显隐。</summary>
    private Control BuildTabbed(LayoutTabbed t, IWorkspace ws)
    {
        var grid = new Grid();
        var views = new List<Control?>();
        foreach (var rid in t.RegionIds)
        {
            var panel = ws.Panels.FirstOrDefault(p => p.RegionId == rid);
            if (panel is null) { views.Add(null); continue; }
            Control? content = null;
            if (_viewRegistry is not null)
            {
                content = _viewRegistry.CreateView(panel.Key);
                if (content is not null) content.DataContext = panel.ViewModel;
            }
            views.Add(content);
            if (content is not null)
            {
                content.IsVisible = false;
                grid.Children.Add(content); // 全在 (0,0)，靠 IsVisible 切换
            }
        }
        if (_workspace is not null)
        {
            UpdateVisiblePanel(views, _workspace.SelectedTabIndex);
            // 仅当此 workspace 仍是活动 workspace 时响应 SelectedTabIndex 变化（防 DataContext 切换后旧 workspace 通知影响废弃视图）。
            var owner = _workspace;
            if (owner is INotifyPropertyChanged pc)
                pc.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(IWorkspace.SelectedTabIndex) && _workspace == owner)
                        UpdateVisiblePanel(views, owner.SelectedTabIndex);
                };
        }
        return grid;
    }

    /// <summary>切显隐：仅 idx 对应的 panel 可见，其余隐藏。idx 越界则全隐。</summary>
    private static void UpdateVisiblePanel(List<Control?> views, int idx)
    {
        for (int i = 0; i < views.Count; i++)
        {
            if (views[i] is { } c)
                c.IsVisible = i == idx;
        }
    }

    // ── 布局持久化（view 收集拖动后实际尺寸 → JSON；VM 侧恢复） ──

    /// <summary>收集当前布局树：回写拖动后 Pixel 列的实际像素宽 + tab 的 SelectedIndex，Star 列保持 Star。
    /// 在宿主窗口 Closing 时调，序列化后存 IPreferencesStore。</summary>
    public LayoutNode? CollectCurrentLayout()
    {
        if (_rootLayout is null) return null;
        return Transform(_rootLayout);
    }

    /// <summary>递归重建布局树，仅更新 Pixel 列宽与 tab 选中索引；结构与 region id 不变。</summary>
    private LayoutNode Transform(LayoutNode n) => n switch
    {
        // 仅能读到已登记的 split 的实际宽（_splitState 按引用相等键控）。
        LayoutSplit s when _splitState.TryGetValue(s, out var st) =>
            s with { Pixels = ReadPixels(s, st), Children = s.Children.Select(Transform).ToArray() },
        LayoutSplit s => s with { Children = s.Children.Select(Transform).ToArray() },
        LayoutTabbed t => t with { SelectedIndex = _workspace?.SelectedTabIndex ?? t.SelectedIndex },
        _ => n, // LayoutRegion：无动态状态
    };

    /// <summary>读 Pixel 列的实际渲染尺寸（子控件 Bounds）；Star 列（Pixels[i] &lt;= 0）原样保留，不转成 Pixel（免失响应式）。
    /// Bounds 未就绪（0）时回退原值。<b>隐藏守卫</b>：terminal/filetree 折叠或切 tab 隐藏后子控件 IsVisible=false → Bounds=0，
    /// 会把持久化像素污染成 0（下次启动树/终端宽 0）。对这两类已知隐藏宿主，用记忆的展开尺寸替换（terminal 行高 / filetree 列宽）。</summary>
    private double[]? ReadPixels(LayoutSplit s, (Grid Grid, List<Control> Children) st)
    {
        if (s.Pixels is null) return null;
        if (s.Proportional) return ReadProportionalWeights(s, st);   // 比例 split 持久化写星权重（窗口尺寸无关）
        var pixels = new double[s.Pixels.Length];
        for (int i = 0; i < s.Pixels.Length; i++)
        {
            if (s.Pixels[i] > 0 && i < st.Children.Count) // 仅 Pixel 列读实际宽
            {
                var child = st.Children[i];
                // 隐藏宿主守卫：折叠/切 tab 隐藏后 Bounds=0，用 _hiddenSizes 记忆的展开尺寸替换。
                double fallback = 0;
                if (!child.IsVisible && _hiddenSizes.TryGetValue(child, out var remembered))
                    fallback = remembered;

                var sz = s.Orientation == SplitOrientation.Horizontal
                    ? child.Bounds.Width : child.Bounds.Height;
                pixels[i] = sz > 0 ? Math.Round(sz) : (fallback > 0 ? fallback : s.Pixels[i]);
            }
            else
            {
                pixels[i] = s.Pixels[i]; // Star 列保持 <=0（Star）
            }
        }
        return pixels;
    }

    /// <summary>Proportional split 的持久化：写<b>星权重</b>（实际宽 / 子项实际总宽——窗口尺寸无关，跨机器/跨重启一致）。
    /// 隐藏子项用 <see cref="_hiddenSizes"/> 记忆的像素当伪实际——比率是相对量，量纲无关（0.35 权重与 420px 伪实际算出同一比率）。
    /// 任一子项既无 Bounds 又无记忆（早于首个布局 pass）→ 原样返回，免写垃圾权重。</summary>
    private double[]? ReadProportionalWeights(LayoutSplit s, (Grid Grid, List<Control> Children) st)
    {
        int n = Math.Min(s.Pixels!.Length, st.Children.Count);
        if (n == 0) return s.Pixels;
        bool horiz = s.Orientation == SplitOrientation.Horizontal;
        var actual = new double[n];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var child = st.Children[i];
            var sz = horiz ? child.Bounds.Width : child.Bounds.Height;
            if (sz <= 0 && !child.IsVisible && _hiddenSizes.TryGetValue(child, out var remembered))
                sz = remembered;   // 隐藏宿主：伪实际像素（权重语义下有效）
            if (sz <= 0) return s.Pixels;
            actual[i] = sz;
            sum += sz;
        }
        if (sum <= 0) return s.Pixels;
        var weights = new double[s.Pixels.Length];
        for (int i = 0; i < weights.Length; i++)
            weights[i] = i < n ? Math.Round(actual[i] / sum, 4) : s.Pixels[i];
        return weights;
    }

    /// <summary>Loaded 时订阅宿主窗口 Closing——与 NotesView 自持久化同模式。
    /// 经 TopLevel 取宿主 Window（WorkspaceView 在 ContentControl 内，TopLevel 即窗口）。</summary>
    private void WireHostClosing(object? sender, EventArgs e)
    {
        if (_persistWired) return;
        if (TopLevel.GetTopLevel(this) is Window host)
        {
            host.Closing += OnHostClosing;
            _persistWired = true;
        }
    }

    /// <summary>宿主窗口关闭时持久化当前布局。阻塞写（SetAsync 真异步，UI 线程用 Task.Run().Wait() 免死锁，同 NotesView）。
    /// 仅活动工作台的 view 持久化——reset 触发新 WorkspaceView，旧 view detached 后 _workspace != Active → 跳过，免覆盖重置。</summary>
    private void OnHostClosing(object? sender, EventArgs e)
    {
        // dock Quit 时 ShutdownRequested 的 await DisposeAsync 先于窗口关闭释放了容器——
        // 裸 GetService 会抛 ObjectDisposedException 成为未处理异常 → SIGABRT 崩溃。改走容错解析，
        // 容器已释放则各步静默跳过（布局持久化本就是 best-effort）。见 DesktopApp.TryGetService。
        var mgr = DesktopApp.TryGetService<IWorkspaceManager>();
        if (_workspace is null) return;
        if (mgr?.Active is not null && !ReferenceEquals(_workspace, mgr.Active)) return; // 非活动工作台（reset 后旧 view）
        var layout = CollectCurrentLayout();
        if (layout is null) return;
        var store = DesktopApp.TryGetService<IPreferencesStore>();
        if (store is null) return;
        var json = JsonSerializer.Serialize(layout, typeof(LayoutNode));
        try { Task.Run(async () => await store.SetAsync(LayoutPrefKey, json)).Wait(); }
        catch { /* 持久化失败不阻断关闭 */ }
    }

    // ── 底部终端面板折叠/展开（方案 B toggle：terminal region 改 Grid 行高，PTY 不中断） ──

    /// <summary>终端面板显隐变更（TerminalService 触发，可能非 UI 线程）→ marshal 到 UI 线程改 region 行高。</summary>
    private void OnTerminalPanelVisibilityChanged(bool visible)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyTerminalVisibility(visible));

    /// <summary>折叠/展开底部终端 region：改其父 Grid 行高（0 ↔ 展开高度）+ splitter 显隐 + content 显隐。
    /// 折叠时 PTY 不中断（仅隐藏 region 空间，会话继续在后台跑）。纯用户 toggle 驱动，与当前 nav tab 无关
    /// （VSCode 式：切 editor tab 不动面板；原 tab-coupled 隐藏 2026-09-22 删——ChatPage 死后无保留理由）。</summary>
    private void ApplyTerminalVisibility(bool visible)
    {
        if (_terminalSplitGrid is null || _terminalRow < 0) return;
        var row = _terminalSplitGrid.RowDefinitions[_terminalRow];
        if (!visible)
        {
            if (row.Height.Value > 0) _terminalExpandedHeight = row.Height; // 记当前展开高度（含用户拖动后/比例星权重）
            row.MinHeight = 0;   // 比例行须临时解除最小高钳制（Grid 对 MinHeight 的钳制优先于 Height=0 → 折不进 0）；像素行本无钳制
            row.Height = new GridLength(0);
            SetSplitterVisible(_terminalSplitGrid, _terminalSplitterRow, false);
            if (_terminalHost is not null)
            {
                // 记忆值存隐藏前的实际像素（Bounds）——比例 split 的 ReadProportionalWeights 拿它当伪实际算权重。
                _hiddenSizes[_terminalHost] = _terminalHost.Bounds.Height > 0 ? _terminalHost.Bounds.Height : 220;
                _terminalHost.IsVisible = false;
            }
        }
        else
        {
            row.Height = _terminalExpandedHeight.Value > 0
                ? _terminalExpandedHeight
                : new GridLength(220, GridUnitType.Pixel);
            if (_terminalProportional) row.MinHeight = TerminalMinHeight;   // 恢复比例行的最小高钳制
            SetSplitterVisible(_terminalSplitGrid, _terminalSplitterRow, true);
            if (_terminalHost is not null)
            {
                _hiddenSizes.Remove(_terminalHost);
                _terminalHost.IsVisible = true;
            }
        }
    }

    /// <summary>按 row 索引设 split Grid 中对应 GridSplitter 的可见性（折叠终端时隐藏其前的分隔条）。</summary>
    private static void SetSplitterVisible(Grid grid, int splitterRow, bool visible)
    {
        if (splitterRow < 0) return;
        foreach (var c in grid.Children)
            if (c is GridSplitter && Grid.GetRow(c) == splitterRow)
                c.IsVisible = visible;
    }

    // ── 侧栏 shell 级左栏条件可见（VSCode 式：documents/notes tab 显示对应树，切其它 tab 隐藏列宽 0） ──

    /// <summary>workspace SelectedTabIndex 变化 → 只切左栏树内容（documents→文件树 / knowledge→.md 树 / …）。
    /// 终端/右栏不随 tab 变——纯用户 toggle（VSCode 式：切 editor tab 不动面板，布局恒定不跳）。</summary>
    private void OnWorkspaceSelectedTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IWorkspace.SelectedTabIndex) || _workspace is null) return;
        ApplyLeftTreeVisibility(ResolveLeftTreeKind(_workspace.SelectedTabIndex));
    }

    /// <summary>当前 tab → 侧栏活动树：documents→"documents" / sourcecontrol→"sourcecontrol" / knowledge→"knowledge" / knowledgescm→"knowledgescm" / 其它→null（隐藏左栏）。</summary>
    private string? ResolveLeftTreeKind(int tabIndex)
    {
        if (tabIndex == _documentsTabIndex) return "documents";
        if (tabIndex == _sourceControlTabIndex) return "sourcecontrol";
        if (tabIndex == _knowledgeTabIndex) return "knowledge";
        if (tabIndex == _knowledgeScmTabIndex) return "knowledgescm";
        return null;
    }

    /// <summary>显/隐侧栏左栏列并切对应树：kind 非空→恢复列宽 + splitter 显 + content 显 + SetActiveTree(kind)；
    /// kind=null→记展开宽 + 列宽 0 + splitter 隐 + content 隐（VSCode 式切到非编辑器页时目录栏消失）。
    /// 另叠加手动 toggle（ILeftTreePanelService，默认开）：手动 toggle 关 → 即使 kind 非空也折叠（标题栏「主侧栏」按钮）。
    /// 恢复时用记忆的展开宽度（含用户拖动后的实际值）。</summary>
    private void ApplyLeftTreeVisibility(string? kind)
    {
        if (_leftTreeSplitGrid is null || _leftTreeCol < 0) return;
        // 手动 toggle（默认开）：关则整体折叠，无视 tab 种类（镜像右栏 ApplyChatVisibility 的 service toggle）。
        bool toggleOn = _leftTreeService?.IsLeftTreeVisible ?? true;
        if (!toggleOn) kind = null;
        // 无 documents/sourcecontrol/knowledge tab（均 -1）→ ResolveLeftTreeKind 恒 null → 永不显示。
        var col = _leftTreeSplitGrid.ColumnDefinitions[_leftTreeCol];
        if (kind is null)
        {
            if (col.Width.Value > 0) _leftTreeExpandedWidth = col.Width; // 记当前展开宽度（含用户拖动后/比例星权重）
            if (_leftTreeHost is not null)
            {
                _hiddenSizes[_leftTreeHost] = _leftTreeHost.Bounds.Width > 0 ? _leftTreeHost.Bounds.Width : 240;
                _leftTreeHost.IsVisible = false;
            }
            col.MinWidth = 0;   // 比例列须临时解除最小宽钳制（否则 Grid 钳住折不进 0）
            col.Width = new GridLength(0);
            SetSplitterVisibleCol(_leftTreeSplitGrid, _leftTreeSplitterCol, false);
        }
        else
        {
            col.Width = _leftTreeExpandedWidth.Value > 0
                ? _leftTreeExpandedWidth
                : new GridLength(240, GridUnitType.Pixel);
            if (_leftTreeProportional) col.MinWidth = LeftTreeMinWidth;   // 恢复比例列的最小宽钳制
            SetSplitterVisibleCol(_leftTreeSplitGrid, _leftTreeSplitterCol, true);
            if (_leftTreeHost is not null)
            {
                _hiddenSizes.Remove(_leftTreeHost);
                _leftTreeHost.IsVisible = true;
            }
            (_leftTreeHost?.Content as LeftTreePanelView)?.SetActiveTree(kind);
        }
    }

    /// <summary>按 col 索引设 split Grid 中对应 GridSplitter 的可见性（隐藏文件树时隐藏其前的分隔条）。</summary>
    private static void SetSplitterVisibleCol(Grid grid, int splitterCol, bool visible)
    {
        if (splitterCol < 0) return;
        foreach (var c in grid.Children)
            if (c is GridSplitter && Grid.GetColumn(c) == splitterCol)
                c.IsVisible = visible;
    }

    /// <summary>拖动结束 → 把三个面板（终端/左栏/聊天右栏）的实际列宽/行高同步回记忆字段。否则拖动只改 Grid 尺寸、
    /// <c>_xxxExpandedWidth/Height</c> 仍是旧值，下次 ApplyXxxVisibility（切导航触发）用旧值覆盖 → 「切导航就重置」。</summary>
    private void SyncExpandedSizes()
    {
        RenormalizeProportionalSplits();
        if (_terminalSplitGrid is not null && _terminalRow >= 0)
            _terminalExpandedHeight = _terminalSplitGrid.RowDefinitions[_terminalRow].Height;
        if (_leftTreeSplitGrid is not null && _leftTreeCol >= 0)
            _leftTreeExpandedWidth = _leftTreeSplitGrid.ColumnDefinitions[_leftTreeCol].Width;
        if (_chatSplitGrid is not null && _chatCol >= 0)
            _chatExpandedWidth = _chatSplitGrid.ColumnDefinitions[_chatCol].Width;
    }

    /// <summary><b>GridSplitter 拖完会把星列改写成像素长度</b>（Avalonia GridSplitter 设 GridLength(px)）——按当前实际尺寸
    /// 重算星权重写回，窗口 resize 才能继续按比例分摊。隐藏子项（宽 0）跳过并保持 0（展开走 _xxxExpanded 记忆恢复）。
    /// 在 SyncExpandedSizes 开头调（先归一化再捕获记忆字段 → 记住的是星长度而非拖后的像素长度）。
    /// 按子项 Bounds 读实际尺寸（拖动期间每帧都有布局 pass，DragCompleted 时 Bounds 即拖后值）。</summary>
    private void RenormalizeProportionalSplits()
    {
        foreach (var (node, st) in _splitState)
        {
            if (node is not LayoutSplit { Proportional: true } s) continue;
            bool horiz = s.Orientation == SplitOrientation.Horizontal;
            int n = st.Children.Count;
            var actual = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var child = st.Children[i];
                if (!child.IsVisible) continue;   // 折叠列保持 0，不参与权重重算
                var sz = horiz ? child.Bounds.Width : child.Bounds.Height;
                if (sz <= 0) continue;            // 未布局——保留旧权重
                actual[i] = sz;
                sum += sz;
            }
            if (sum <= 0) continue;
            for (int i = 0; i < n; i++)
            {
                if (actual[i] <= 0) continue;
                var weight = Math.Round(actual[i] / sum, 4);
                if (horiz) st.Grid.ColumnDefinitions[i * 2].Width = new GridLength(weight, GridUnitType.Star);
                else st.Grid.RowDefinitions[i * 2].Height = new GridLength(weight, GridUnitType.Star);
            }
        }
    }

    /// <summary>Proportional split 子区的最小尺寸（防窗口缩到最小时星列被压死）。已知侧区按 region id 查表；
    /// tabbed/嵌套 split = 主内容区用主区下限。窗口 MinWidth 900 下可共存：180+300+320+6×2=812 ≤ 900-72(rail)。</summary>
    private static double? MinSizeFor(LayoutNode child, bool horiz)
    {
        var regionId = (child as LayoutRegion)?.RegionId;
        return regionId switch
        {
            PanelKeys.LeftTree => horiz ? (double?)LeftTreeMinWidth : null,
            PanelKeys.Chat => horiz ? (double?)ChatMinWidth : null,
            PanelKeys.Terminal => horiz ? null : TerminalMinHeight,
            _ => horiz ? MainMinWidth : MainMinHeight,   // 主内容区（tabbed/嵌套 split）与未知 region 的通用下限
        };
    }

    // ── 右侧 AI 聊天面板折叠/展开（VSCode 式可折叠侧栏：chat region 改 Grid 列宽，聊天视图不销毁，流式不中断） ──

    /// <summary>聊天面板显隐变更（ChatPanelService 触发，可能非 UI 线程）→ marshal 到 UI 线程重算列宽。</summary>
    private void OnChatVisibilityChanged(bool visible)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyChatVisibility());

    /// <summary>主侧栏手动 toggle 变更（LeftTreePanelService 触发，可能非 UI 线程）→ marshal 到 UI 线程重算（当前 tab 已解析，kind 仍按当前 tab 判定，叠加 toggle）。</summary>
    private void OnLeftTreeVisibilityChanged(bool visible)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyLeftTreeVisibility(ResolveLeftTreeKind(_workspace?.SelectedTabIndex ?? -1)));

    /// <summary>折叠/展开右侧聊天 region：改其父 Grid 列宽（0 ↔ 展开宽度）+ splitter 显隐 + content 显隐。
    /// 折叠时聊天视图不销毁（仅隐藏 region 空间，流式回复继续在后台）。镜像 <see cref="ApplyLeftTreeVisibility"/>，
    /// 但不切子树（聊天只有单一视图，无 documents/notes 那种树切换）。复用 <see cref="SetSplitterVisibleCol"/>，不新增方法。
    /// 纯用户 toggle 驱动，与当前 nav tab 无关（VSCode 式：切 editor tab 不动面板，布局恒定不跳；
    /// 原 tab-coupled 隐藏 2026-09-22 删——其为 ChatPage 双聊天框防重复而生，ChatPage 死后无保留理由）。</summary>
    private void ApplyChatVisibility()
    {
        if (_chatSplitGrid is null || _chatCol < 0) return;
        bool visible = _chatService?.IsChatVisible ?? true; // 默认开（不回归）
        var col = _chatSplitGrid.ColumnDefinitions[_chatCol];
        if (!visible)
        {
            if (col.Width.Value > 0) _chatExpandedWidth = col.Width; // 记当前展开宽度（含用户拖动后/比例星权重）
            if (_chatHost is not null)
            {
                // 须在 IsVisible=false 前设 _hiddenSizes（供 ReadPixels/ReadProportionalWeights 取回退值——折叠态不持久化 0）：
                // 存隐藏前的实际像素 Bounds（比例 split 当伪实际算权重）。
                _hiddenSizes[_chatHost] = _chatHost.Bounds.Width > 0 ? _chatHost.Bounds.Width : 420;
                _chatHost.IsVisible = false;
            }
            col.MinWidth = 0;   // 比例列须临时解除最小宽钳制（否则 Grid 钳住折不进 0）
            col.Width = new GridLength(0);
            SetSplitterVisibleCol(_chatSplitGrid, _chatSplitterCol, false);
        }
        else
        {
            col.Width = _chatExpandedWidth.Value > 0
                ? _chatExpandedWidth
                : new GridLength(420, GridUnitType.Pixel);
            if (_chatProportional) col.MinWidth = ChatMinWidth;   // 恢复比例列的最小宽钳制
            SetSplitterVisibleCol(_chatSplitGrid, _chatSplitterCol, true);
            if (_chatHost is not null)
            {
                _hiddenSizes.Remove(_chatHost);
                _chatHost.IsVisible = true;
            }
        }
    }
}
