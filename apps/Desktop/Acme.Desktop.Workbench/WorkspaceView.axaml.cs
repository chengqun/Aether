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

    private IViewRegistry? _viewRegistry;
    private IRegionManager? _regionManager;
    private IWorkspace? _workspace;

    /// <summary>正在构建的布局树根（收集时遍历它回写拖动后的实际尺寸）。</summary>
    private LayoutNode? _rootLayout;

    /// <summary>每个 LayoutSplit 节点 → 它的 Grid + 子控件列表（读子控件 Bounds.Width/Height 取拖动后实际像素宽）。
    /// record 默认按值相等会撞键，必须用引用相等（ReferenceEqualityComparer）。</summary>
    private readonly Dictionary<LayoutNode, (Grid Grid, List<Control> Children)> _splitState
        = new(ReferenceEqualityComparer.Instance);

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
        if (_workspace is null)
        {
            _rootLayout = null;
            Content = null;
            return;
        }

        EnsureServices();
        _rootLayout = _workspace.Layout;
        Content = _rootLayout is not null ? BuildLayout(_rootLayout, _workspace) : null;
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
                // Pixels[i] > 0 → 固定像素（如 chat 420px）；null 或 <= 0 → Star（弹性，如内容区）。
                len = (s.Pixels is not null && childIdx < s.Pixels.Length && s.Pixels[childIdx] > 0)
                    ? new GridLength(s.Pixels[childIdx], GridUnitType.Pixel)
                    : new GridLength(1, GridUnitType.Star);
            }
            if (horiz) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = len });
            else grid.RowDefinitions.Add(new RowDefinition { Height = len });
        }
        // 子控件先建好再登记 split 状态——收集时读子控件 Bounds.Width/Height 取拖动后实际像素宽。
        var children = new List<Control>(n);
        for (int i = 0; i < n; i++)
        {
            var child = BuildLayout(s.Children[i], ws);
            children.Add(child);
            int idx = i * 2;
            if (horiz) Grid.SetColumn(child, idx); else Grid.SetRow(child, idx);
            grid.Children.Add(child);
            if (i < n - 1)
            {
                var splitter = new GridSplitter
                {
                    ResizeDirection = horiz ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    Background = Brushes.Transparent,
                    Focusable = false,
                };
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
    /// Bounds 未就绪（0）时回退原值。</summary>
    private static double[]? ReadPixels(LayoutSplit s, (Grid Grid, List<Control> Children) st)
    {
        if (s.Pixels is null) return null;
        var pixels = new double[s.Pixels.Length];
        for (int i = 0; i < s.Pixels.Length; i++)
        {
            if (s.Pixels[i] > 0 && i < st.Children.Count) // 仅 Pixel 列读实际宽
            {
                var sz = s.Orientation == SplitOrientation.Horizontal
                    ? st.Children[i].Bounds.Width : st.Children[i].Bounds.Height;
                pixels[i] = sz > 0 ? Math.Round(sz) : s.Pixels[i];
            }
            else
            {
                pixels[i] = s.Pixels[i]; // Star 列保持 <=0（Star）
            }
        }
        return pixels;
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
        var mgr = DesktopApp.Services?.GetService<IWorkspaceManager>();
        if (_workspace is null) return;
        if (mgr?.Active is not null && !ReferenceEquals(_workspace, mgr.Active)) return; // 非活动工作台（reset 后旧 view）
        var layout = CollectCurrentLayout();
        if (layout is null) return;
        var store = DesktopApp.Services?.GetService<IPreferencesStore>();
        if (store is null) return;
        var json = JsonSerializer.Serialize(layout, typeof(LayoutNode));
        try { Task.Run(async () => await store.SetAsync(LayoutPrefKey, json)).Wait(); }
        catch { /* 持久化失败不阻断关闭 */ }
    }
}
