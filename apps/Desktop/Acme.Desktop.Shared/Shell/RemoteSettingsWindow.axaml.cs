using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>Git 远程设置窗口。不存储任何 PAT——凭证完全由系统 git credential helper
/// 管理（macOS keychain / Windows Credential Manager / Linux libsecret）。此窗口职责：
/// ① 绑定/更换远端（<c>git remote add origin &lt;url&gt;</c> + 可选 <c>git branch -M</c> + 复用 <see cref="IGitService.Push"/> 首推设上游）
/// ② 探测系统凭证状态（已认证/未认证/未绑定远端）③ 测试连接（fetch 验证）。
/// <para>仓库路径由 ctor 参 <c>repoPath</c> 指定（VM 传入——项目 SCM 传项目目录、知识对象 SCM 传知识库目录），
/// 不再硬解析 <see cref="ICurrentProjectDirectoryProvider"/>——修「从知识对象 SCM 打开会误探项目目录」的旧 bug。</para></summary>
public partial class RemoteSettingsWindow : ThemedWindow
{
    private IGitService? _git;
    private string? _repoPath;

    public RemoteSettingsWindow(string? repoPath)
    {
        _repoPath = repoPath;
        InitializeComponent();
    }

    /// <summary>无参 ctor（设计器/旧路径兜底；等价于 repoPath=null —— 窗口显示「未打开仓库」）。</summary>
    public RemoteSettingsWindow() : this(null) { }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var sp = DesktopApp.Services;
        if (sp is null) return;
        _git = sp.GetService<IGitService>();
        try
        {
            await LoadRemoteUrlAsync();
            await ProbeAuthStatusAsync();
        }
        catch (Exception ex) { SetAuthBanner(false, $"初始化失败：{ex.Message}"); }
    }

    /// <summary>预填远端地址框（已绑定则显示当前 URL，未绑定留空）。不联网。</summary>
    private async Task LoadRemoteUrlAsync()
    {
        if (_git is null || string.IsNullOrEmpty(_repoPath)) return;
        var url = await Task.Run(() => _git!.GetRemoteUrl(_repoPath!));
        RemoteUrlBox.Text = url ?? string.Empty;
    }

    /// <summary>探测目标仓库的系统凭证状态，更新状态条 + 详情/指引。无 remote 时显中性「未绑定远端」。</summary>
    private async Task ProbeAuthStatusAsync()
    {
        if (_git is null) { SetAuthBanner(false, "凭证服务未就绪。"); HelpBox.IsVisible = false; return; }
        if (string.IsNullOrEmpty(_repoPath) || !_git.IsRepository(_repoPath))
        {
            SetAuthBanner(false, "当前未打开 git 仓库，无法检测。请先在文档/知识对象页打开一个 git 仓库。");
            HelpBox.IsVisible = false;
            DetailText.Text = string.Empty;
            return;
        }
        // 无远端 → 中性提示（非 auth-fail），引导用上方绑定区。
        var remoteUrl = await Task.Run(() => _git!.GetRemoteUrl(_repoPath!));
        if (string.IsNullOrEmpty(remoteUrl))
        {
            SetAuthBannerNeutral("未绑定远端。在上方输入远端地址后点「绑定并推送」即可首次发布。");
            HelpBox.IsVisible = false;
            DetailText.Text = "绑定后若首次推送因凭证失败，按下方指引在终端执行一次 git push 让系统保存该站点凭证。";
            return;
        }
        bool hasSystem;
        try { hasSystem = await Task.Run(() => _git!.HasSystemCredentials(_repoPath!)); }
        catch { hasSystem = false; }

        if (hasSystem)
        {
            SetAuthBanner(true, $"已通过系统 git 凭证认证（{remoteUrl}）。push / pull / sync 直接可用。");
            DetailText.Text = "凭证由系统 git credential helper 管理（macOS keychain / Windows Credential Manager / Linux libsecret）。Aether 不存储任何 GitHub 密码或 PAT，与命令行 git 共用同一凭证。";
            HelpBox.IsVisible = false;
        }
        else
        {
            SetAuthBanner(false, $"系统未找到 {remoteUrl} 的 git 凭证。");
            DetailText.Text = "Aether 复用系统 git 的凭证（与命令行 git push 同源），不在应用内存储 PAT。请先在终端让 git 保存一次该站点的凭证：";
            HelpBox.IsVisible = true;
            HelpCmd.Text = $"git push"; // 绑定该站点的 remote 后，终端 push 一次即让 git 存凭证
        }
    }

    /// <summary>绑定/更换远端 + 可选分支重命名 + 首推（复用 Push 自动设上游）。
    /// 顺序：AddRemote（本地）→ RenameCurrentBranch（本地，可选）→ Push（联网，可能凭证失败）。每步结果回显。</summary>
    private async void OnBindClick(object? sender, RoutedEventArgs e)
    {
        if (_git is null || string.IsNullOrEmpty(_repoPath))
        {
            SetAuthBanner(false, "凭证服务未就绪或未打开仓库。");
            return;
        }
        var url = RemoteUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            SetAuthBanner(false, "请输入远端地址。");
            return;
        }
        var branch = BranchBox.Text?.Trim();

        BindButton.IsEnabled = false;
        TestButton.IsEnabled = false;
        SetAuthBanner(true, "绑定并推送中…");
        try
        {
            // 1. 添加/更新远端（本地操作）。
            var (addOk, addMsg) = await Task.Run(() => _git!.AddRemote(_repoPath!, "origin", url!));
            if (!addOk)
            {
                SetAuthBanner(false, addMsg);
                return;
            }

            // 2. 可选分支重命名（本地操作）。失败则远端已绑定，提示但跳过 push。
            if (!string.IsNullOrEmpty(branch))
            {
                var (rnOk, rnMsg) = await Task.Run(() => _git!.RenameCurrentBranch(_repoPath!, branch!));
                if (!rnOk)
                {
                    SetAuthBanner(false, $"远端已绑定，但分支重命名失败：{rnMsg}");
                    await LoadRemoteUrlAsync();
                    return;
                }
            }

            // 3. 首推（联网，复用 Push 的 refspec + 设上游）。凭证缺失会失败——显示 HelpBox 指引。
            var (pushOk, pushMsg) = await Task.Run(() => _git!.Push(_repoPath!));
            SetAuthBanner(pushOk, pushMsg);
            if (!pushOk && pushMsg.Contains("凭证", StringComparison.Ordinal))
            {
                // 凭证失败：远端已绑定成功，引导终端 push 存凭证。HelpCmd 指向具体命令。
                HelpBox.IsVisible = true;
                HelpCmd.Text = $"git push -u origin HEAD";
                DetailText.Text = "远端已绑定，但首次推送因系统未存该站点凭证而失败。请在终端执行上述命令（git 会保存凭证），之后回面板 push/pull/sync 即可。";
            }
            // 成功 → 重探 auth + 重载 URL 框（按钮文案驱动用）。
            if (pushOk)
            {
                await LoadRemoteUrlAsync();
                await ProbeAuthStatusAsync();
            }
        }
        catch (Exception ex) { SetAuthBanner(false, $"绑定推送失败：{ex.Message}"); }
        finally
        {
            BindButton.IsEnabled = true;
            TestButton.IsEnabled = true;
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

    /// <summary>中性状态条（未绑定远端等非成败状态）：琥珀图标 + 中性背景，不暗示成功/失败。</summary>
    private void SetAuthBannerNeutral(string text)
    {
        AuthStatusText.Text = text;
        AuthBanner.Background = this.FindResource("CardBgBrush") as Avalonia.Media.IBrush;
        AuthIcon.Foreground = this.FindResource("TextSecondaryBrush") as Avalonia.Media.IBrush;
    }

    /// <summary>测试连接：fetch 验证当前系统凭证是否可用。结果回显到状态条（成功绿/失败黄）。</summary>
    private async void OnTestClick(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            if (_git is null || string.IsNullOrEmpty(_repoPath) || !_git.IsRepository(_repoPath))
            {
                SetAuthBanner(false, "当前未打开 git 仓库，无法测试。");
                return;
            }
            SetAuthBanner(true, "测试中…");
            var (ok, msg) = await Task.Run(() => _git.Fetch(_repoPath!));
            SetAuthBanner(ok, msg);
        }
        catch (Exception ex) { SetAuthBanner(false, $"测试失败：{ex.Message}"); }
        finally { TestButton.IsEnabled = true; }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
