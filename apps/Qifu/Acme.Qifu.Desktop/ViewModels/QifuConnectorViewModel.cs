using Acme.AppAuth.Application.Auth;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Yushu;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// Qifu 连接器 ViewModel：毓数平台认证 + 360Teams 云文档认证（+ 同步到服务器）。
/// 实现 <see cref="IConnectorViewModel"/> 供 ExpertsViewModel 持有。UI 无关。
/// 镜像 AlphaAgent 的 Qifu ConnectorViewModel。
/// </summary>
public sealed partial class QifuConnectorViewModel : ObservableObject, IConnectorViewModel
{
    private readonly IYushuAuthService _yushuAuth;
    private readonly IYushuTokenSyncService _yushuSync;
    private readonly ICloudDocsAuthService _cloudDocsAuth;
    private readonly ICloudDocsTokenSyncService _cloudDocsSync;
    private readonly IAppAuthService _auth;

    public QifuConnectorViewModel(
        IYushuAuthService yushuAuth, IYushuTokenSyncService yushuSync,
        ICloudDocsAuthService cloudDocsAuth, ICloudDocsTokenSyncService cloudDocsSync,
        IAppAuthService auth)
    {
        _yushuAuth = yushuAuth ?? throw new ArgumentNullException(nameof(yushuAuth));
        _yushuSync = yushuSync ?? throw new ArgumentNullException(nameof(yushuSync));
        _cloudDocsAuth = cloudDocsAuth ?? throw new ArgumentNullException(nameof(cloudDocsAuth));
        _cloudDocsSync = cloudDocsSync ?? throw new ArgumentNullException(nameof(cloudDocsSync));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _ = CheckAllAsync();
    }

    // —— 登录态（门控"同步到服务器"按钮可见性） ——
    [ObservableProperty] private bool _isLoggedIn;
    public bool ShowYushuSync => IsYushuAuthenticated && IsLoggedIn;
    public bool ShowCloudDocsSync => IsCloudDocsAuthenticated && IsLoggedIn;

    partial void OnIsYushuAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(ShowYushuSync));
    partial void OnIsCloudDocsAuthenticatedChanged(bool value) => OnPropertyChanged(nameof(ShowCloudDocsSync));
    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowYushuSync));
        OnPropertyChanged(nameof(ShowCloudDocsSync));
    }

    // —— 毓数平台 ——
    [ObservableProperty] private bool _isYushuAuthenticated;
    [ObservableProperty] private bool _isYushuSynced;
    [ObservableProperty] private string _yushuStatusText = "未登录";
    [ObservableProperty] private bool _isYushuLoggingIn;
    [ObservableProperty] private bool _isYushuSyncing;

    // —— 云文档 ——
    [ObservableProperty] private bool _isCloudDocsAuthenticated;
    [ObservableProperty] private bool _isCloudDocsSynced;
    [ObservableProperty] private string _cloudDocsStatusText = "未登录";
    [ObservableProperty] private bool _isCloudDocsLoggingIn;
    [ObservableProperty] private bool _isCloudDocsSyncing;

    /// <summary>刷新登录态（同步按钮可见性）。</summary>
    public async Task RefreshLoginStateAsync()
    {
        try { IsLoggedIn = await _auth.IsLoggedInAsync(); }
        catch { IsLoggedIn = false; }
    }

    /// <summary>切到连接器 tab 时刷新全部状态。</summary>
    public async Task RefreshAsync()
    {
        await RefreshLoginStateAsync();
        await CheckAllAsync();
    }

    private async Task CheckAllAsync()
    {
        try { IsYushuAuthenticated = await _yushuAuth.IsAuthenticatedAsync(); }
        catch { IsYushuAuthenticated = false; }
        YushuStatusText = FormatStatus(IsYushuAuthenticated, IsYushuSynced);

        try { IsCloudDocsAuthenticated = await _cloudDocsAuth.IsAuthenticatedAsync(); }
        catch { IsCloudDocsAuthenticated = false; }
        CloudDocsStatusText = FormatStatus(IsCloudDocsAuthenticated, IsCloudDocsSynced);
    }

    private static string FormatStatus(bool authed, bool synced)
        => authed ? (synced ? "已登录 · 已同步" : "已登录（未同步）") : "未登录";

    // —— 毓数命令 ——
    [RelayCommand]
    private async Task YushuLoginAsync()
    {
        if (IsYushuLoggingIn) return;
        IsYushuLoggingIn = true;
        YushuStatusText = "登录中…（浏览器 OAuth）";
        try
        {
            var token = await _yushuAuth.LoginAsync();
            if (token is null)
            {
                YushuStatusText = "登录失败";
                IsYushuAuthenticated = false;
            }
            else
            {
                IsYushuAuthenticated = true;
                IsYushuSynced = false;
                YushuStatusText = "已登录（未同步）";
            }
        }
        catch (Exception ex) { YushuStatusText = $"登录失败：{ex.Message}"; }
        finally { IsYushuLoggingIn = false; }
    }

    [RelayCommand]
    private async Task YushuSyncToServerAsync()
    {
        if (IsYushuSyncing) return;
        IsYushuSyncing = true;
        try
        {
            var ok = await _yushuSync.SyncToServerAsync();
            IsYushuSynced = ok;
            YushuStatusText = ok ? "已登录 · 已同步" : "同步失败";
        }
        catch (Exception ex) { YushuStatusText = $"同步失败：{ex.Message}"; }
        finally { IsYushuSyncing = false; }
    }

    // —— 云文档命令 ——
    [RelayCommand]
    private async Task CloudDocsLoginAsync()
    {
        if (IsCloudDocsLoggingIn) return;
        IsCloudDocsLoggingIn = true;
        CloudDocsStatusText = "登录中…（浏览器 SSO）";
        try
        {
            var result = await _cloudDocsAuth.LoginAsync();
            if (result?.Success == true)
            {
                IsCloudDocsAuthenticated = true;
                IsCloudDocsSynced = false;
                CloudDocsStatusText = "已登录（未同步）";
            }
            else
            {
                IsCloudDocsAuthenticated = false;
                CloudDocsStatusText = $"登录失败：{result?.Error ?? "未知"}（首次需 playwright install chromium）";
            }
        }
        catch (Exception ex) { CloudDocsStatusText = $"登录失败：{ex.Message}"; }
        finally { IsCloudDocsLoggingIn = false; }
    }

    [RelayCommand]
    private async Task CloudDocsSyncToServerAsync()
    {
        if (IsCloudDocsSyncing) return;
        IsCloudDocsSyncing = true;
        try
        {
            var ok = await _cloudDocsSync.SyncToServerAsync();
            IsCloudDocsSynced = ok;
            CloudDocsStatusText = ok ? "已登录 · 已同步" : "同步失败";
        }
        catch (Exception ex) { CloudDocsStatusText = $"同步失败：{ex.Message}"; }
        finally { IsCloudDocsSyncing = false; }
    }
}
