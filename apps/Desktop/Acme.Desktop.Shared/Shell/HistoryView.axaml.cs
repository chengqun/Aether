using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Line = Avalonia.Controls.Shapes.Line;
using Path = Avalonia.Controls.Shapes.Path;

namespace Acme.Desktop.Shared.Shell;

/// <summary>提交历史拓扑图渲染。镜像 <see cref="WorkflowGraphWindow"/> 的 Canvas 子控件命令式画法：
/// 模型→布局在 <see cref="CommitGraphBuilder"/>（纯函数），此处仅把 <see cref="CommitGraphModel"/> 翻译成 Canvas 子控件
///（segments→Line/Path、节点→Ellipse、commit 行→Button）。订阅 VM 的 <c>HistoryGraph</c>/<c>SelectedCommit</c>
/// 变更重渲染。连线/节点固定 palette 色（明暗通用对比度），文字用 DynamicResource token 跟主题。</summary>
public partial class HistoryView : UserControl
{
    private const double RowH = 28;     // 行高
    private const double LaneW = 22;    // 车道宽
    private const double LeftPad = 14;  // 左留白
    private const double NodeR = 5;     // 节点半径
    private const double TextGap = 16;  // 图区到文本起始的间距
    private const double MinTextWidth = 520;

    private SourceControlViewModel? _vm;

    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as SourceControlViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            RenderGraph(_vm.HistoryGraph);
        }
        else
        {
            GraphCanvas.Children.Clear();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(SourceControlViewModel.HistoryGraph)
            or nameof(SourceControlViewModel.SelectedCommit))
        {
            RenderGraph(_vm.HistoryGraph);
        }
    }

    private void RenderGraph(CommitGraphModel? model)
    {
        var canvas = GraphCanvas;
        canvas.Children.Clear();
        if (model is null || model.Commits.Count == 0) return;

        var graphW = LeftPad * 2 + model.LaneCount * LaneW;
        var textStart = graphW + TextGap;
        var width = textStart + MinTextWidth;
        var height = model.Commits.Count * RowH;
        canvas.Width = width;
        canvas.Height = height;

        var selectedSha = _vm?.SelectedCommit?.Info.Sha;

        // 1. commit 行（Button）—— 最底层，占满整行，点选触发 SelectCommit。
        foreach (var c in model.Commits)
            canvas.Children.Add(MakeRow(c, textStart, width - textStart, c.Info.Sha == selectedSha));

        // 2. 连线段（Line/Path）—— 不拦截鼠标，让点击穿透到行按钮。
        foreach (var seg in model.Segments)
            canvas.Children.Add(MakeSegment(seg, model.LaneCount));

        // 3. 节点（Ellipse）—— 顶层但 IsHitTestVisible=false。
        foreach (var c in model.Commits)
            canvas.Children.Add(MakeNode(c, c.Info.Sha == selectedSha));

        // 4. ref 徽标（HEAD→分支 / 分支 / 标签）—— 节点旁，顶层不拦截鼠标。
        foreach (var c in model.Commits)
        {
            var badge = MakeRefBadges(c, textStart);
            if (badge is not null) canvas.Children.Add(badge);
        }
    }

    private static double LaneX(int lane) => LeftPad + lane * LaneW + LaneW / 2;
    private static double RowY(int row) => row * RowH + RowH / 2;

    private Control MakeRow(LaidOutCommit c, double textStart, double textWidth, bool isSelected)
    {
        var button = new Button
        {
            Classes = { "graph-row" },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(textStart, 0, 0, 0),
            Width = textStart + textWidth,
            Height = RowH,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Command = _vm?.SelectCommitCommand,
            CommandParameter = c,
            Content = MakeRowContent(c, textWidth),
        };
        if (isSelected)
            button.Classes.Add("selected");
        Canvas.SetTop(button, c.Row * RowH);
        Canvas.SetLeft(button, 0);
        return button;
    }

    private static Control MakeRowContent(LaidOutCommit c, double textWidth)
    {
        // sha(短,mono,dim) + message(主体,trim) + author(dim) + date(dim)
        var sha = new TextBlock
        {
            Text = c.Info.ShaShort,
            FontFamily = new FontFamily("Menlo,Monaco,Consolas,monospace"),
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var msg = new TextBlock
        {
            Text = c.Info.Message,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 60,
        };
        var author = new TextBlock
        {
            Text = c.Info.Author,
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120,
        };
        var date = new TextBlock
        {
            Text = c.Info.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FontSize = 11,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(date, Dock.Right);
        DockPanel.SetDock(author, Dock.Right);
        panel.Children.Add(date);
        panel.Children.Add(author);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(sha);
        left.Children.Add(msg);
        panel.Children.Add(left);
        return panel;
    }

    private static Control MakeSegment(GraphSegment seg, int laneCount)
    {
        var brush = new SolidColorBrush(Color.FromUInt32(seg.Color));
        var fromX = LaneX(seg.FromLane);
        var toX = LaneX(seg.ToLane);
        var fromY = RowY(seg.FromRow);
        var toY = RowY(seg.ToRow);

        if (seg.Kind == SegmentKind.Vertical)
        {
            var line = new Line
            {
                StartPoint = new Point(fromX, fromY),
                EndPoint = new Point(toX, toY),
                Stroke = brush,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            return line;
        }

        // Elbow：立方贝塞尔，控制点纵向偏移半行，形成平滑弧。
        var fig = new PathFigure { StartPoint = new Point(fromX, fromY), IsClosed = false };
        fig.Segments.Add(new BezierSegment
        {
            Point1 = new Point(fromX, fromY + RowH / 2),
            Point2 = new Point(toX, toY - RowH / 2),
            Point3 = new Point(toX, toY),
            IsStroked = true,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var path = new Path
        {
            Data = geo,
            Stroke = brush,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        return path;
    }

    private static Control MakeNode(LaidOutCommit c, bool isSelected)
    {
        var fill = new SolidColorBrush(Color.FromUInt32(PaletteColor(c.Lane)));
        var node = new Ellipse
        {
            Width = NodeR * 2,
            Height = NodeR * 2,
            Fill = fill,
            IsHitTestVisible = false,
        };
        if (isSelected)
        {
            node.Stroke = new SolidColorBrush(Color.FromUInt32(0xFFFFFFFF));
            node.StrokeThickness = 2;
        }
        // 有分支/标签指向的节点描一圈深色边，更醒目。
        else if (c.Refs.Count > 0)
        {
            node.Stroke = new SolidColorBrush(Color.FromUInt32(0xFF1F2937));
            node.StrokeThickness = 1.5;
        }
        Canvas.SetLeft(node, LaneX(c.Lane) - NodeR);
        Canvas.SetTop(node, RowY(c.Row) - NodeR);
        return node;
    }

    private static Control? MakeRefBadges(LaidOutCommit c, double textStart)
    {
        if (c.Refs.Count == 0) return null;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, IsHitTestVisible = false };
        foreach (var r in c.Refs)
        {
            panel.Children.Add(MakeBadge(r));
        }
        Canvas.SetLeft(panel, textStart - 6);
        Canvas.SetTop(panel, c.Row * RowH + (RowH - 16) / 2);
        return panel;
    }

    private static Control MakeBadge(CommitRef r)
    {
        var (text, bg, fg) = r switch
        {
            { IsHead: true } => ($"HEAD → {r.Name}", new SolidColorBrush(Color.FromUInt32(0xFF2563EB)), Brushes.White),
            { IsTag: true } => (r.Name, new SolidColorBrush(Color.FromUInt32(0xFFD97706)), Brushes.White),
            { IsRemote: true } => (r.Name, new SolidColorBrush(Color.FromUInt32(0xFF6B7280)), Brushes.White),
            _ => (r.Name, new SolidColorBrush(Color.FromUInt32(0xFF059669)), Brushes.White),
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = bg,
            Padding = new Thickness(4, 1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        return border;
    }

    // 与 CommitGraphBuilder.Palette 同步（渲染侧本地副本，避免跨层暴露私有数组）。
    private static uint PaletteColor(int lane) => (lane & 7) switch
    {
        0 => 0xFF42A5F5,
        1 => 0xFF66BB6A,
        2 => 0xFFAB47BC,
        3 => 0xFFFFA726,
        4 => 0xFF26C6DA,
        5 => 0xFFEF5350,
        6 => 0xFFD4E157,
        _ => 0xFF8D6E63,
    };
}
