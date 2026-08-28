using System;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IToastService"/> 实现：往宿主 overlay <see cref="Panel"/>（WorkbenchWindow 右下角）动态加气泡，自动淡出。
/// 单例——一个窗口一个 toast 宿主。不占布局（overlay 层叠于 shell 之上，IsHitTestVisible=false 不拦截交互）。
/// </summary>
/// <remarks>
/// 宿主由 WorkbenchWindow 在 OnOpened 时经 <see cref="AttachHost"/> 注册（须窗口已加载才有可视树）；
/// 未注册宿主时 <see cref="Show"/> 静默丢弃（桌面尚未就绪时安全）。宿主 overlay 是
/// WorkbenchWindow 最外层 Grid 的 Layer 2（在 DreamLayer aurora 层 + DockPanel shell 层之上、右下角对齐）。
/// </remarks>
public sealed class ToastService : IToastService
{
    private Panel? _host;
    private readonly object _lock = new();

    /// <summary>气泡自动消失时长（毫秒）。错误略长，成功/信息略短。</summary>
    private const int InfoTimeoutMs = 2600;
    private const int ErrorTimeoutMs = 4200;

    /// <summary>注册右下角 overlay 宿主 Panel（WorkbenchWindow 构建可视树后调用）。可重复调用以更新宿主。</summary>
    public void AttachHost(Panel host) { lock (_lock) _host = host; }

    /// <summary>解除宿主（窗口关闭时）。</summary>
    public void DetachHost() { lock (_lock) _host = null; }

    /// <inheritdoc/>
    public void Show(string? message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Panel? host;
        lock (_lock) host = _host;
        if (host is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (host is null) return;
            var border = BuildBubble(message!, kind);
            host.Children.Add(border);
            ScheduleDismiss(host, border, kind);
        });
    }

    private static Border BuildBubble(string message, ToastKind kind)
    {
        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Foreground = BrushFor(KindForegroundKey(kind)),
        };
        var border = new Border
        {
            Padding = new Thickness(14, 9),
            CornerRadius = new CornerRadius(8),
            Background = BrushFor(KindBackgroundKey(kind)),
            BorderBrush = BrushFor(KindBorderKey(kind)),
            BorderThickness = new Thickness(1),
            Child = text,
            // 弹入淡入
            Opacity = 0,
        };
        return border;
    }

    private static void ScheduleDismiss(Panel host, Border bubble, ToastKind kind)
    {
        // 淡入
        bubble.Opacity = 1;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        var start = Environment.TickCount64;
        var total = kind == ToastKind.Error ? ErrorTimeoutMs : InfoTimeoutMs;
        var elapsed = 0;
        timer.Tick += (_, _) =>
        {
            elapsed += 80;
            // 末尾 600ms 淡出
            if (elapsed >= total - 600)
            {
                var progress = (double)(elapsed - (total - 600)) / 600;
                bubble.Opacity = Math.Max(0, 1 - progress);
            }
            if (elapsed >= total)
            {
                timer.Stop();
                host.Children.Remove(bubble);
            }
        };
        timer.Start();
    }

    private static string KindBackgroundKey(ToastKind kind) => kind switch
    {
        ToastKind.Success => "SuccessBgBrush",
        ToastKind.Error => "DangerBgBrush",
        _ => "CardBgBrush",
    };

    private static string KindBorderKey(ToastKind kind) => kind switch
    {
        ToastKind.Success => "SuccessColor",
        ToastKind.Error => "DangerBrush",
        _ => "BorderBrush",
    };

    private static string KindForegroundKey(ToastKind kind) => kind switch
    {
        ToastKind.Success => "SuccessColor",
        ToastKind.Error => "DangerBrush",
        _ => "TextPrimaryBrush",
    };

    private static IBrush BrushFor(string key)
        => Application.Current!.TryGetResource(key, out var r) && r is IBrush b
            ? b
            : Brushes.Gray;
}
