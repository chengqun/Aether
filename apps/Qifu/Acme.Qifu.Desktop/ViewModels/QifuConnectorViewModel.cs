using Acme.DataQuery.Application;
using Acme.DataQuery.Domain;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Yushu;
using Acme.Desktop.Mvvm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// Qifu 复合连接器：实现 <see cref="IConnectorViewModel"/>，内部并列四张卡片——
/// 毓数平台认证 + mcphub 网关认证 + 360Teams 云文档认证 + MySQL 直连。
/// 由共享 ItemsControl + WrapPanel 数据驱动渲染（<see cref="Cards"/>）。
/// 前三张各自持有认证服务，「+」直接登录（浏览器 OAuth / SSO）；
/// MySQL 卡「+」弹设置窗填连接信息 + 测试连接（镜像 Stockapp 的 CTP/同花顺卡）。
/// </summary>
public sealed class QifuConnectorViewModel : IConnectorViewModel
{
    public IReadOnlyList<ConnectorCardViewModel> Cards { get; }

    public QifuConnectorViewModel(
        IYushuAuthService yushuAuth,
        ICloudDocsAuthService cloudDocsAuth,
        IMcphubAuthService mcphubAuth,
        IConnectorSettingsDialogService? connectorSettingsDialogService = null,
        IPreferencesStore? preferences = null,
        DataQueryOptions? mysqlOptions = null,
        ISqlQueryService? sqlQueryService = null,
        IYushuTokenSyncService? yushuTokenSyncService = null,
        ICloudDocsTokenSyncService? cloudDocsTokenSyncService = null,
        IMcphubTokenSyncService? mcphubTokenSyncService = null)
    {
        Cards = new List<ConnectorCardViewModel>
        {
            new YushuConnectorCardViewModel(yushuAuth, yushuTokenSyncService),
            new McphubConnectorCardViewModel(mcphubAuth, mcphubTokenSyncService),
            new CloudDocsConnectorCardViewModel(cloudDocsAuth, cloudDocsTokenSyncService),
            new MySqlConnectorCardViewModel(connectorSettingsDialogService, preferences, mysqlOptions, sqlQueryService),
        };
    }

    /// <summary>切到连接器 tab 时逐张刷新状态。</summary>
    public async Task RefreshAsync()
    {
        foreach (var card in Cards)
            await card.RefreshAsync();
    }
}

/// <summary>毓数平台认证卡片。「+」触发浏览器 OAuth 登录；已认证且存在同步服务时显示「同步」。</summary>
internal sealed partial class YushuConnectorCardViewModel : ConnectorCardViewModel
{
    private readonly IYushuAuthService _auth;
    private readonly IYushuTokenSyncService? _sync;

    public YushuConnectorCardViewModel(IYushuAuthService auth, IYushuTokenSyncService? sync)
    {
        _auth = auth;
        _sync = sync;
        Icon = "🔢";
        IconBackground = "#E3F2FD";
        Title = "毓数平台认证";
        Description = "连接毓数平台以使用 SQL 数据查询功能";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
        if (_sync is not null)
            SyncCommand = new AsyncRelayCommand(SyncAsync);
        CheckAsync().SafeFireAndForget();
    }

    [ObservableProperty] private bool _isLoggingIn;
    partial void OnIsLoggingInChanged(bool value) => IsPrimaryEnabled = !value;

    public override async Task RefreshAsync() => await CheckAsync();

    private async Task CheckAsync()
    {
        try { IsConnected = await _auth.IsAuthenticatedAsync(); }
        catch { IsConnected = false; }
        StatusText = IsConnected ? "已连接" : "未连接";
        UpdateSyncVisibility();
    }

    /// <summary>已认证 + 有同步服务时才显示「同步」。（IsConnected 来自基类，其 OnIsConnectedChanged
    /// 分部方法无法在派生类实现，故在每次状态变更后显式刷新。）</summary>
    private void UpdateSyncVisibility() => ShowSync = _sync is not null && IsConnected;

    private async Task LoginAsync()
    {
        if (IsLoggingIn) return;
        IsLoggingIn = true;
        StatusText = "登录中…（浏览器 OAuth）";
        try
        {
            var token = await _auth.LoginAsync();
            if (token is null)
            {
                IsConnected = false;
                StatusText = "登录失败";
            }
            else
            {
                IsConnected = true;
                StatusText = "已连接";
            }
        }
        catch (Exception ex) { StatusText = $"登录失败：{ex.Message}"; }
        finally { IsLoggingIn = false; UpdateSyncVisibility(); }
    }

    private async Task SyncAsync()
    {
        if (_sync is null) return;
        StatusText = "同步中…";
        try
        {
            var success = await _sync.SyncToServerAsync();
            StatusText = success ? "已同步到服务器" : "同步失败";
        }
        catch (Exception ex) { StatusText = $"同步失败：{ex.Message}"; }
    }
}

/// <summary>mcphub 网关认证卡片。「+」触发浏览器 OAuth 登录（loopback 回调，写入
/// ~/.local/share/mcphub/credentials.json，与 Python skill 共享）。SQL 查询 / 权限工单工具统一经 mcphub 网关用此凭据。
/// 已认证且存在同步服务时显示「同步」——把 JWT 上传服务器，供手机端拉取（镜像毓数卡的「从服务器同步 token」）。</summary>
internal sealed partial class McphubConnectorCardViewModel : ConnectorCardViewModel
{
    private readonly IMcphubAuthService _auth;
    private readonly IMcphubTokenSyncService? _sync;

    public McphubConnectorCardViewModel(IMcphubAuthService auth, IMcphubTokenSyncService? sync)
    {
        _auth = auth;
        _sync = sync;
        Icon = "🛰️";
        IconBackground = "#EDE7F6";
        Title = "mcphub 网关认证";
        Description = "连接 mcphub 网关以使用 SQL 查询 / 权限工单工具";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
        if (_sync is not null)
            SyncCommand = new AsyncRelayCommand(SyncAsync);
        CheckAsync().SafeFireAndForget();
    }

    [ObservableProperty] private bool _isLoggingIn;
    partial void OnIsLoggingInChanged(bool value) => IsPrimaryEnabled = !value;

    public override async Task RefreshAsync() => await CheckAsync();

    private async Task CheckAsync()
    {
        try { IsConnected = await _auth.IsAuthenticatedAsync(); }
        catch { IsConnected = false; }
        StatusText = IsConnected ? "已连接" : "未连接";
        UpdateSyncVisibility();
    }

    /// <summary>已认证 + 有同步服务时才显示「同步」。</summary>
    private void UpdateSyncVisibility() => ShowSync = _sync is not null && IsConnected;

    private async Task LoginAsync()
    {
        if (IsLoggingIn) return;
        IsLoggingIn = true;
        StatusText = "登录中…（浏览器 OAuth）";
        try
        {
            var token = await _auth.LoginAsync();
            if (token is null)
            {
                IsConnected = false;
                StatusText = "登录失败";
            }
            else
            {
                IsConnected = true;
                StatusText = "已连接";
            }
        }
        catch (Exception ex) { StatusText = $"登录失败：{ex.Message}"; }
        finally { IsLoggingIn = false; UpdateSyncVisibility(); }
    }

    private async Task SyncAsync()
    {
        if (_sync is null) return;
        StatusText = "同步中…";
        try
        {
            // 从本地凭据文件读当前 JWT 后上传（登录成功时已 fire-and-forget 上传过；按钮供手动重同步）。
            var token = await _auth.GetAccessTokenAsync();
            var success = await _sync.SyncToServerAsync(token, null);
            StatusText = success ? "已同步到服务器" : "同步失败";
        }
        catch (Exception ex) { StatusText = $"同步失败：{ex.Message}"; }
    }
}

/// <summary>360Teams 云文档认证卡片。「+」触发浏览器 SSO 登录；已认证且存在同步服务时显示「同步」。</summary>
internal sealed partial class CloudDocsConnectorCardViewModel : ConnectorCardViewModel
{
    private readonly ICloudDocsAuthService _auth;
    private readonly ICloudDocsTokenSyncService? _sync;

    public CloudDocsConnectorCardViewModel(ICloudDocsAuthService auth, ICloudDocsTokenSyncService? sync)
    {
        _auth = auth;
        _sync = sync;
        Icon = "☁️";
        IconBackground = "#E8F5E9";
        Title = "云文档认证";
        Description = "连接 360Teams 云文档以使用文件上传功能";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
        if (_sync is not null)
            SyncCommand = new AsyncRelayCommand(SyncAsync);
        CheckAsync().SafeFireAndForget();
    }

    [ObservableProperty] private bool _isLoggingIn;
    partial void OnIsLoggingInChanged(bool value) => IsPrimaryEnabled = !value;

    public override async Task RefreshAsync() => await CheckAsync();

    private async Task CheckAsync()
    {
        try { IsConnected = await _auth.IsAuthenticatedAsync(); }
        catch { IsConnected = false; }
        StatusText = IsConnected ? "已连接" : "未连接";
        UpdateSyncVisibility();
    }

    /// <summary>已认证 + 有同步服务时才显示「同步」。（IsConnected 来自基类，其 OnIsConnectedChanged
    /// 分部方法无法在派生类实现，故在每次状态变更后显式刷新。）</summary>
    private void UpdateSyncVisibility() => ShowSync = _sync is not null && IsConnected;

    private async Task LoginAsync()
    {
        if (IsLoggingIn) return;
        IsLoggingIn = true;
        StatusText = "登录中…（浏览器 SSO）";
        try
        {
            var result = await _auth.LoginAsync();
            if (result?.Success == true)
            {
                IsConnected = true;
                StatusText = "已连接";
            }
            else
            {
                IsConnected = false;
                StatusText = $"登录失败：{result?.Error ?? "未知"}（首次需 playwright install chromium）";
            }
        }
        catch (Exception ex) { StatusText = $"登录失败：{ex.Message}"; }
        finally { IsLoggingIn = false; UpdateSyncVisibility(); }
    }

    private async Task SyncAsync()
    {
        if (_sync is null) return;
        StatusText = "同步中…";
        try
        {
            var success = await _sync.SyncToServerAsync();
            StatusText = success ? "已同步到服务器" : "同步失败";
        }
        catch (Exception ex) { StatusText = $"同步失败：{ex.Message}"; }
    }
}
