using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>Git 远程凭证诊断窗口。不存储任何 PAT——凭证完全由系统 git credential helper
/// 管理（macOS keychain / Windows Credential Manager / Linux libsecret）。此窗口仅：
/// ① 探测系统凭证状态（已认证/未认证）② 未认证时给出终端配置指引 ③ 测试连接（fetch 验证）。
/// 经 <see cref="DesktopApp.Services"/> 解析 <see cref="IGitService"/> + <see cref="ICurrentProjectDirectoryProvider"/>。</summary>
public partial class RemoteSettingsWindow : ThemedWindow
{
    private IGitService? _git;
    private ICurrentProjectDirectoryProvider? _projectDir;

    public RemoteSettingsWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var sp = DesktopApp.Services;
        if (sp is null) return;
        _git = sp.GetService<IGitService>();
        _projectDir = sp.GetService<ICurrentProjectDirectoryProvider>();
        try { await ProbeAuthStatusAsync(); }
        catch (Exception ex) { SetAuthBanner(false, $"凭证检测失败：{ex.Message}"); }
    }

    /// <summary>探测当前项目的系统凭证状态，更新状态条 + 详情/指引。</summary>
    private async Task ProbeAuthStatusAsync()
    {
        if (_git is null) { SetAuthBanner(false, "凭证服务未就绪。"); HelpBox.IsVisible = false; return; }
        var dir = _projectDir?.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
        {
            SetAuthBanner(false, "当前未打开 git 项目，无法检测凭证。请先在文档页打开项目。");
            HelpBox.IsVisible = false;
            return;
        }
        bool hasSystem;
        try { hasSystem = await Task.Run(() => _git.HasSystemCredentials(dir)); }
        catch { hasSystem = false; }

        if (hasSystem)
        {
            SetAuthBanner(true, "已通过系统 git 凭证认证。push / pull / sync 直接可用。");
            DetailText.Text = "凭证由系统 git credential helper 管理（macOS keychain / Windows Credential Manager / Linux libsecret）。Aether 不存储任何 GitHub 密码或 PAT，与命令行 git 共用同一凭证。";
            HelpBox.IsVisible = false;
        }
        else
        {
            SetAuthBanner(false, "系统未找到 git 凭证。");
            DetailText.Text = "Aether 复用系统 git 的凭证（与命令行 git push 同源），不在应用内存储 PAT。请先在终端让 git 保存一次凭证：";
            HelpBox.IsVisible = true;
            HelpCmd.Text = "git push";
        }
    }

    private void SetAuthBanner(bool ok, string text)
    {
        AuthStatusText.Text = text;
        AuthBanner.Background = ok ? this.FindResource("SuccessBgBrush") as Avalonia.Media.IBrush
                                   : this.FindResource("WarningBgBrush") as Avalonia.Media.IBrush;
        AuthIcon.Foreground = ok ? this.FindResource("SuccessBrush") as Avalonia.Media.IBrush
                                 : this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
    }

    /// <summary>测试连接：fetch 验证当前系统凭证是否可用。结果回显到状态条（成功绿/失败黄）。</summary>
    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            var dir = _projectDir?.CurrentDirectory;
            if (string.IsNullOrEmpty(dir) || _git is null || !_git.IsRepository(dir))
            {
                SetAuthBanner(false, "当前未打开 git 项目，无法测试（请先在文档页打开项目）。");
                return;
            }
            SetAuthBanner(true, "测试中…");
            var (ok, msg) = await Task.Run(() => _git.Fetch(dir));
            SetAuthBanner(ok, msg);
        }
        catch (Exception ex) { SetAuthBanner(false, $"测试失败：{ex.Message}"); }
        finally { TestButton.IsEnabled = true; }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
