using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Yushu;
using Acme.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// Qifu 连接器 ViewModel：毓数平台认证 + 360Teams 云文档认证。
/// 实现 <see cref="IConnectorViewModel"/> 供 ExpertsViewModel 持有。UI 无关。
/// </summary>
public sealed partial class QifuConnectorViewModel : ObservableObject, IConnectorViewModel
{
    private readonly IYushuAuthService _yushuAuth;
    private readonly ICloudDocsAuthService _cloudDocsAuth;
    private readonly IYushuTokenSyncService? _yushuTokenSyncService;
    private readonly ICloudDocsTokenSyncService? _cloudDocsTokenSyncService;

    public QifuConnectorViewModel(
        IYushuAuthService yushuAuth,
        ICloudDocsAuthService cloudDocsAuth,
        IYushuTokenSyncService? yushuTokenSyncService = null,
        ICloudDocsTokenSyncService? cloudDocsTokenSyncService = null)
    {
        _yushuAuth = yushuAuth ?? throw new ArgumentNullException(nameof(yushuAuth));
        _cloudDocsAuth = cloudDocsAuth ?? throw new ArgumentNullException(nameof(cloudDocsAuth));
        _yushuTokenSyncService = yushuTokenSyncService;
        _cloudDocsTokenSyncService = cloudDocsTokenSyncService;
        _ = CheckAllAsync();
    }

    // —— 毓数平台 ——
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ShowYushuSync))]
    private bool _isYushuAuthenticated;
    [ObservableProperty] private string _yushuStatusText = "未登录";
    [ObservableProperty] private bool _isYushuLoggingIn;

    // —— 云文档 ——
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(ShowCloudDocsSync))]
    private bool _isCloudDocsAuthenticated;
    [ObservableProperty] private string _cloudDocsStatusText = "未登录";
    [ObservableProperty] private bool _isCloudDocsLoggingIn;

    // —— 同步按钮可见性 ——
    public bool ShowYushuSync => _yushuTokenSyncService != null && IsYushuAuthenticated;
    public bool ShowCloudDocsSync => _cloudDocsTokenSyncService != null && IsCloudDocsAuthenticated;

    /// <summary>切到连接器 tab 时刷新全部状态。</summary>
    public async Task RefreshAsync()
    {
        await CheckAllAsync();
    }

    private async Task CheckAllAsync()
    {
        try { IsYushuAuthenticated = await _yushuAuth.IsAuthenticatedAsync(); }
        catch { IsYushuAuthenticated = false; }
        YushuStatusText = IsYushuAuthenticated ? "已连接" : "未连接";

        try { IsCloudDocsAuthenticated = await _cloudDocsAuth.IsAuthenticatedAsync(); }
        catch { IsCloudDocsAuthenticated = false; }
        CloudDocsStatusText = IsCloudDocsAuthenticated ? "已连接" : "未连接";
    }

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
                YushuStatusText = "已连接";
            }
        }
        catch (Exception ex) { YushuStatusText = $"登录失败：{ex.Message}"; }
        finally { IsYushuLoggingIn = false; }
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
                CloudDocsStatusText = "已连接";
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

    // —— 同步命令 ——
    [RelayCommand]
    private async Task YushuSyncToServerAsync()
    {
        if (_yushuTokenSyncService == null) return;
        YushuStatusText = "同步中…";
        try
        {
            var success = await _yushuTokenSyncService.SyncToServerAsync();
            YushuStatusText = success ? "已同步到服务器" : "同步失败";
        }
        catch (Exception ex) { YushuStatusText = $"同步失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task CloudDocsSyncToServerAsync()
    {
        if (_cloudDocsTokenSyncService == null) return;
        CloudDocsStatusText = "同步中…";
        try
        {
            var success = await _cloudDocsTokenSyncService.SyncToServerAsync();
            CloudDocsStatusText = success ? "已同步到服务器" : "同步失败";
        }
        catch (Exception ex) { CloudDocsStatusText = $"同步失败：{ex.Message}"; }
    }
}
