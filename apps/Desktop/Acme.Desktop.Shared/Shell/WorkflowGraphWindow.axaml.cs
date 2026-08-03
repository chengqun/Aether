using System;
using System.Collections.Generic;
using Acme.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Acme.Desktop.Shared.Shell;

public partial class WorkflowGraphWindow : Window
{
    private readonly LaidOutGraph _graph;
    private readonly string _mermaid;
    private readonly string _windowTitle;
    private readonly string _hint;

    public WorkflowGraphWindow() { _graph = LaidOutGraph.Empty; _mermaid = string.Empty; _windowTitle = "图"; _hint = ""; InitializeComponent(); } // 设计器

    public WorkflowGraphWindow(LaidOutGraph graph, string mermaid,
        string windowTitle = "工作流转交图",
        string hint = "节点=参与者，箭头=转交边（标注=原因），高亮=入口")
    {
        _graph = graph;
        _mermaid = mermaid;
        _windowTitle = windowTitle;
        _hint = hint;
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        Title = _windowTitle;
        if (HintText is not null) HintText.Text = _hint;
        var canvas = GraphCanvas;
        canvas.Children.Clear();
        canvas.Width = Math.Max(_graph.Width, 200);
        canvas.Height = Math.Max(_graph.Height, 200);

        var nodeById = new Dictionary<Guid, LaidOutNode>();
        foreach (var n in _graph.Nodes)
        {
            nodeById[n.Id] = n;
            canvas.Children.Add(MakeNode(n));
        }
        foreach (var e in _graph.Edges)
        {
            canvas.Children.Add(MakeEdge(e));
            canvas.Children.Add(MakeArrow(e));
            if (!string.IsNullOrWhiteSpace(e.Label))
                canvas.Children.Add(MakeLabel(e.Label!, e.LabelX, e.LabelY));
        }
    }

    private static Control MakeNode(LaidOutNode n)
    {
        var isEntry = n.IsEntry;
        var border = new Border
        {
            Width = n.W,
            Height = n.H,
            CornerRadius = new CornerRadius(8),
            Background = isEntry ? new SolidColorBrush(Color.FromRgb(0x08, 0x91, 0xB2)) : Brushes.White,
            BorderBrush = isEntry ? new SolidColorBrush(Color.FromRgb(0x0e, 0x74, 0x90)) : new SolidColorBrush(Color.FromRgb(0xD4, 0xD8, 0xDD)),
            BorderThickness = isEntry ? new Thickness(2) : new Thickness(1),
            Padding = new Thickness(8, 0),
            Child = new TextBlock
            {
                Text = n.Name,
                FontSize = 12,
                FontWeight = isEntry ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = isEntry ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            BoxShadow = isEntry ? new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 2, Blur = 6, Color = Color.FromArgb(80, 0x08, 0x91, 0xB2) }) : default,
        };
        Canvas.SetLeft(border, n.X);
        Canvas.SetTop(border, n.Y);
        return border;
    }

    private static Control MakeEdge(LaidOutEdge e)
    {
        var fig = new PathFigure
        {
            StartPoint = new Point(e.FromX, e.FromY),
            IsClosed = false,
        };
        fig.Segments.Add(new BezierSegment
        {
            Point1 = new Point(e.C1X, e.C1Y),
            Point2 = new Point(e.C2X, e.C2Y),
            Point3 = new Point(e.ToX, e.ToY),
            IsStroked = true,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        var path = new Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            StrokeThickness = 1.5,
        };
        return path;
    }

    private static Control MakeArrow(LaidOutEdge e)
    {
        // 切线方向：t=1 处 ≈ (To - C2)，退化则 (To - From)
        var dx = e.ToX - e.C2X;
        var dy = e.ToY - e.C2Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) { dx = e.ToX - e.FromX; dy = e.ToY - e.FromY; len = Math.Sqrt(dx * dx + dy * dy); }
        if (len < 1) { return new Control { IsVisible = false }; }
        dx /= len; dy /= len;
        var px = -dy; var py = dx;          // 垂直
        var size = 8d;
        var tip = new Point(e.ToX, e.ToY);
        var baseC = new Point(e.ToX - dx * size, e.ToY - dy * size);
        var b1 = new Point(baseC.X + px * size * 0.55, baseC.Y + py * size * 0.55);
        var b2 = new Point(baseC.X - px * size * 0.55, baseC.Y - py * size * 0.55);
        var poly = new Polygon
        {
            Points = new Points { tip, b1, b2 },
            Fill = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
        };
        return poly;
    }

    private static Control MakeLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
        };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1),
            Child = tb,
        };
        // 估算宽度居中（measure 前近似）
        var approxW = text.Length * 7.0 + 12;
        Canvas.SetLeft(border, x - approxW / 2);
        Canvas.SetTop(border, y - 9);
        return border;
    }

    private async void OnCopyMermaidClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await ClipboardExtensions.SetTextAsync(clipboard, _mermaid);
    }
}
