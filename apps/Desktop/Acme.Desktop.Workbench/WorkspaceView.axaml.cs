using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
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
    private IViewRegistry? _viewRegistry;
    private IRegionManager? _regionManager;
    private IWorkspace? _workspace;

    public WorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _workspace = DataContext as IWorkspace;
        if (_workspace is null)
        {
            Content = null;
            return;
        }

        EnsureServices();
        Content = _workspace.Layout is not null ? BuildLayout(_workspace.Layout, _workspace) : null;
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

    /// <summary>单 panel 叶子 + 命名 region 登记。插件可经 RegionManager 往此 region 注入 panel。</summary>
    private Control BuildRegion(LayoutRegion r, IWorkspace ws)
    {
        var cc = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        // 先登记 region 再设 Content——RegisterRegion 会清空宿主 Content（确保 region 起始为空），
        // 若先设 Content 再登记，ChatView 会被 RegisterRegion 的 host.Content=null 抹掉 → 聊天面板空白。
        _regionManager?.RegisterRegion(r.RegionId, cc);
        var panel = ws.Panels.FirstOrDefault(p => p.RegionId == r.RegionId);
        if (panel is not null && _viewRegistry is not null)
        {
            var view = _viewRegistry.CreateView(panel.Key);
            if (view is not null)
            {
                view.DataContext = panel.ViewModel;
                cc.Content = view;
            }
        }
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
        for (int i = 0; i < n; i++)
        {
            var child = BuildLayout(s.Children[i], ws);
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
}
