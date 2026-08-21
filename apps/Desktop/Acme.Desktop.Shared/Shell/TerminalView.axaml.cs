using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Terminal.Theming;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 集成终端视图：标签条（绑 TerminalViewModel.Sessions）+ 控件宿主（每会话一个 RoyalTerminal <see cref="TerminalControl"/>）。
/// 控件/PTY 生命周期由本视图拥有；<see cref="TerminalService"/> 经 AttachView 拿本视图引用并转发 Create/Close/SendInput/RunAsync。
/// 工作台加载即建本视图（IsVisible 切换不销毁）→ 跨 nav 切换常驻，PTY 不断。主题随 ActualThemeVariant。
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly Dictionary<Guid, TerminalControl> _controls = new();
    private TerminalService? _service;
    private bool _initialized;

    public TerminalView()
    {
        InitializeComponent();
        this.Loaded += OnLoaded;
        this.ActualThemeVariantChanged += (_, _) => ApplyThemeToAll();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        _service = DesktopApp.Services?.GetService<ITerminalService>() as TerminalService;
        if (_service is null) return;
        _service.AttachView(this);
        // 首次打开若无会话，自动建一个（cwd 跟随文档页项目目录）。
        if (_service.GetSessions().Count == 0)
        {
            var projectDir = DesktopApp.Services?.GetService<ICurrentProjectDirectoryProvider>()?.CurrentDirectory;
            _service.CreateSession(string.IsNullOrWhiteSpace(projectDir) ? null : projectDir, null);
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _service?.DetachView(this);
    }

    /// <summary>折叠按钮：通知 service 隐藏底部终端面板（PTY 不中断，仅折叠 region 空间；侧边栏 toggle 可再展开）。</summary>
    private void OnCollapseClick(object? sender, RoutedEventArgs e)
        => _service?.SetPanelVisible(false);

    // —— TerminalService 调用的视图面 API ——

    /// <summary>为会话创建终端控件 + 起真实登录 shell。已存在则 no-op。</summary>
    public void CreateSessionControl(ITerminalSession session)
    {
        if (_controls.ContainsKey(session.Id)) return;
        var ctrl = new TerminalControl
        {
            IsVisible = false,
            TerminalFontSize = 13,
        };
        ApplyTheme(ctrl);
        ctrl.TitleChanged += (_, title) => _service?.NotifyTitleChanged(session.Id, title ?? session.Title);
        ctrl.ProcessExited += (_, code) => _service?.NotifyExited(session.Id);
        ContentHost.Children.Add(ctrl);
        _controls[session.Id] = ctrl;

        var (shell, args) = ResolveShell();
        var cwd = string.IsNullOrWhiteSpace(session.Cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : session.Cwd;
        try
        {
            ctrl.StartPty(shell, cwd, args);
        }
        catch (Exception ex)
        {
            // 起壳失败不崩——向控件写一条错误（可见）+ 标记退出。
            ctrl.WriteOutput(System.Text.Encoding.UTF8.GetBytes($"终端启动失败: {ex.Message}\r\n"));
            _service?.NotifyExited(session.Id);
        }
    }

    public void RemoveControl(Guid id)
    {
        if (!_controls.Remove(id, out var ctrl)) return;
        try { ctrl.StopPty(); } catch { }
        try { ctrl.RequestClose(); } catch { }
        ContentHost.Children.Remove(ctrl);
    }

    public void Activate(Guid id)
    {
        foreach (var (cid, ctrl) in _controls)
            ctrl.IsVisible = cid == id;
    }

    public void SendInput(Guid id, string text)
    {
        if (_controls.TryGetValue(id, out var ctrl))
            ctrl.SendInput(text);
    }

    public TerminalControl? GetControl(Guid id) => _controls.TryGetValue(id, out var c) ? c : null;

    /// <summary>视图从树分离（窗口关闭/工作台重置）：杀所有 PTY + 清控件。会话元数据保留在 service（可重建）。</summary>
    public void DisposeAllControls()
    {
        foreach (var ctrl in _controls.Values)
        {
            try { ctrl.StopPty(); } catch { }
            try { ctrl.RequestClose(); } catch { }
            ContentHost.Children.Remove(ctrl);
        }
        _controls.Clear();
    }

    /// <summary>仅杀所有 PTY 子进程 + 关 PTY 句柄，**不动视觉树**（不 Remove 子控件）。
    /// 供 TerminalService.DisposeAsync 在 app 退出时调：容器 dispose 可能跑在非 UI 线程，
    /// <c>ContentHost.Children.Remove</c> 是 Avalonia 视觉树操作须在 UI 线程，故此法只动进程句柄（线程无关）。
    /// 正常退出时视觉树分离（OnDetachedFromVisualTree→DetachView→DisposeAllControls）已先清 PTY，
    /// 此处是兜底：若 dispose 先于树分离发生（视图仍附着），补杀残留 PTY 子进程防退出后 shell 孤儿。</summary>
    public void StopAllPtys()
    {
        foreach (var ctrl in _controls.Values)
        {
            try { ctrl.StopPty(); } catch { }
            try { ctrl.RequestClose(); } catch { }
        }
    }

    // —— 主题 ——
    private void ApplyThemeToAll()

    {
        foreach (var ctrl in _controls.Values)
            ApplyTheme(ctrl);
    }

    private void ApplyTheme(TerminalControl ctrl)
    {
        var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        // ★ RoyalTerminal 正规换肤走 TerminalTheme（record，经 ApplyThemeToRenderer 整体重刷）。
        // 旧的只改 ctrl.DefaultBackground/DefaultForeground 是 legacy 桥接（不完整）——「第一次可、
        // 切回不行、回车才有」即 legacy 桥接只局部生效。这里取当前 Theme 用 With 链改两个默认色
        // （其余 palette/cursor/selection 保持库默认），赋回 ctrl.Theme 触发正规整体重刷。
        // 色值 uint = AARRGGBB（Avalonia Color 转 uint）。
        uint argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        ctrl.Theme = ctrl.Theme
            .WithDefaultBackground(dark ? argb(Color.Parse("#1E1E1E")) : argb(Color.Parse("#FFFFFF")))
            .WithDefaultForeground(dark ? argb(Color.Parse("#D4D4D4")) : argb(Color.Parse("#1E1E1E")));
    }

    // —— shell 解析（交互式登录 shell：Unix zsh/bash -l；Windows powershell）——
    private static (string shell, IReadOnlyList<string> args) ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("powershell.exe", new[] { "-NoLogo" });
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/bash";
        return (shell, new[] { "-l" });
    }
}
