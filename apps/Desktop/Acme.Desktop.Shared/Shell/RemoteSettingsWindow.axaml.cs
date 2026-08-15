using System;
using System.Linq;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>Git 远程设置窗口（GitHub 用户名 + PAT）。经 <see cref="DesktopApp.Services"/> 解析
/// <see cref="IPreferencesStore"/>（持久化 git.username/git.token）+ <see cref="IGitService"/>（测试连接）。</summary>
public partial class RemoteSettingsWindow : ThemedWindow
{
    private IPreferencesStore? _prefs;
    private IGitService? _git;
    private ICurrentProjectDirectoryProvider? _projectDir;

    public RemoteSettingsWindow()
    {
        InitializeComponent();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 从 DI 解析服务（DesktopApp.Services 由租户 App 构建容器后赋值；设计期 null 跳过）。
        var sp = DesktopApp.Services;
        if (sp is null) return;
        _prefs = sp.GetService<IPreferencesStore>();
        _git = sp.GetService<IGitService>();
        _projectDir = sp.GetService<ICurrentProjectDirectoryProvider>();
        // 预填已存 PAT（仅后备用）。
        if (_prefs is not null)
        {
            UsernameBox.Text = _prefs.GetAsync("git.username").GetAwaiter().GetResult();
            TokenBox.Text = _prefs.GetAsync("git.token").GetAwaiter().GetResult();
        }
        // 探测系统 git credential helper 是否已认证（macOS keychain 等）→ 显示真实认证状态。
        await ProbeAuthStatusAsync();
    }

    /// <summary>探测当前项目的系统凭证状态，更新顶部状态条（已认证=绿/未认证=黄）。</summary>
    private async Task ProbeAuthStatusAsync()
    {
        if (_git is null) { SetAuthBanner(false, "凭证服务未就绪。"); return; }
        var dir = _projectDir?.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
        {
            SetAuthBanner(false, "当前未打开 git 项目，无法检测系统凭证。请先在文档页打开项目。");
            return;
        }
        bool hasSystem;
        try { hasSystem = await Task.Run(() => _git.HasSystemCredentials(dir)); }
        catch { hasSystem = false; }

        if (hasSystem)
            SetAuthBanner(true, "已通过系统 git 凭证认证（如 macOS keychain）。push/pull/sync 直接可用，无需填写下方 PAT。");
        else
            SetAuthBanner(false, "系统未找到 git 凭证。请填写下方 GitHub 用户名 + PAT（生成方法见说明），保存后即可用。");
    }

    private void SetAuthBanner(bool ok, string text)
    {
        AuthStatusText.Text = text;
        AuthBanner.Background = ok ? this.FindResource("SuccessBgBrush") as Avalonia.Media.IBrush
                                   : this.FindResource("WarningBgBrush") as Avalonia.Media.IBrush;
        AuthIcon.Foreground = ok ? this.FindResource("SuccessBrush") as Avalonia.Media.IBrush
                                 : this.FindResource("WarningBrush") as Avalonia.Media.IBrush;
    }

    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        if (!await SaveFieldsAsync()) return;
        StatusText.Text = "测试中…";
        TestButton.IsEnabled = false;
        try
        {
            var dir = _projectDir?.CurrentDirectory;
            if (string.IsNullOrEmpty(dir) || _git is null || !_git.IsRepository(dir))
            {
                StatusText.Text = "当前未打开 git 项目，无法测试（请先在文档页打开项目）。";
                return;
            }
            var (ok, msg) = await Task.Run(() => _git.Fetch(dir));
            StatusText.Text = msg;
        }
        finally { TestButton.IsEnabled = true; }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (!await SaveFieldsAsync()) return;
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>把当前输入框值写入 preferences。返回是否已配置服务（否则提示）。</summary>
    private async Task<bool> SaveFieldsAsync()
    {
        if (_prefs is null)
        {
            StatusText.Text = "偏好存储未就绪。";
            return false;
        }
        // SetAsync 真异步（File.WriteAllTextAsync），用 Task.Run 避开 UI 线程 SynchronizationContext 死锁。
        await Task.Run(async () =>
        {
            await _prefs.SetAsync("git.username", string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text?.Trim());
            await _prefs.SetAsync("git.token", string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text?.Trim());
        });
        StatusText.Text = "已保存。";
        return true;
    }
}
