using System;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels.Connector;

// 手机端连接器 auth 卡片：毓数 / 云文档 / mcphub 网关。
//
// 逻辑逐字搬自 AiSettingsViewModel 的「从服务器同步」路径（手机端 token = 从服务器拉桌面端已上传的 token/sid/JWT，
// 无浏览器 OAuth——浏览器登录是桌面端连接器的活）。手机端只拉不上传，故无 SyncCommand（区别桌面端 card）。
//
// 三卡的服务端口（IYushuAuthService / ICloudDocsAuthService(+ICloudDocsTokenSyncService) / IMcphubAuthService）
// 在 Acme.Mobile.Mvvm.Services 定义，由 Qifu.Mobile 宿主提供适配器实现桥接到 Acme.Qifu.* 业务层。
// Stockapp 不注册这些适配器 → 这些卡不会进 DI → ConnectorViewModel.Cards 不含它们（IEnumerable 解析时空）。

/// <summary>毓数平台连接器卡（手机端从服务器同步 token）。</summary>
public sealed partial class YushuConnectorCard : ConnectorCardViewModel
{
    private readonly IYushuAuthService _auth;
    private readonly IToastService? _toast;

    public YushuConnectorCard(IYushuAuthService auth, IToastService? toast)
    {
        _auth = auth;
        _toast = toast;
        Icon = "🔢";
        IconBackground = "#E3F2FD";
        Title = "毓数平台";
        Description = "从服务器同步 token，供毓数 SQL 查询工具鉴权";
        PrimaryButtonText = "从服务器同步";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
    }

    public override async Task RefreshAsync()
    {
        try
        {
            var isAuth = await Task.Run(async () => await _auth.IsAuthenticatedAsync());
            IsConnected = isAuth;
            StatusText = isAuth ? "已认证" : "未认证";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YushuConnectorCard] 检查认证状态失败: {ex.Message}");
            IsConnected = false;
            StatusText = "状态未知";
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsPrimaryInProgress) return;
        IsPrimaryInProgress = true;
        StatusText = "正在从服务器同步...";
        try
        {
            var token = await Task.Run(async () => await _auth.LoginAsync());
            if (token != null)
            {
                IsConnected = true;
                StatusText = "已认证";
                await (_toast?.ShowAsync("毓数平台同步成功") ?? Task.CompletedTask);
            }
            else
            {
                StatusText = "同步失败（服务器无有效token）";
                await (_toast?.ShowAsync("同步失败，请重试") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YushuConnectorCard] 登录失败: {ex.Message}");
            StatusText = "操作失败";
            await (_toast?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsPrimaryInProgress = false;
        }
    }
}

/// <summary>云文档连接器卡（手机端从服务器拉 sid）。
/// 与毓数/mcphub 不同：手机端登录 = <see cref="ICloudDocsTokenSyncService.SyncFromServerAsync"/> 拉取后
/// 再 <see cref="ICloudDocsAuthService.IsAuthenticatedAsync"/> 校验 sid（与旧 AiSettingsViewModel 手机分支一致）。</summary>
public sealed partial class CloudDocsConnectorCard : ConnectorCardViewModel
{
    private readonly ICloudDocsAuthService _auth;
    private readonly ICloudDocsTokenSyncService? _tokenSync;
    private readonly IToastService? _toast;

    public CloudDocsConnectorCard(ICloudDocsAuthService auth, ICloudDocsTokenSyncService? tokenSync, IToastService? toast)
    {
        _auth = auth;
        _tokenSync = tokenSync;
        _toast = toast;
        Icon = "☁️";
        IconBackground = "#E1F5FE";
        Title = "云文档";
        Description = "从服务器同步 sid，供云文档工具鉴权";
        PrimaryButtonText = "从服务器同步";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
    }

    public override async Task RefreshAsync()
    {
        try
        {
            var isAuth = await Task.Run(async () => await _auth.IsAuthenticatedAsync());
            IsConnected = isAuth;
            StatusText = isAuth ? "已认证" : "未认证";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudDocsConnectorCard] 检查认证状态失败: {ex.Message}");
            IsConnected = false;
            StatusText = "状态未知";
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsPrimaryInProgress) return;
        if (_tokenSync is null)
        {
            StatusText = "同步服务未注册";
            await (_toast?.ShowAsync("同步服务未注册") ?? Task.CompletedTask);
            return;
        }
        IsPrimaryInProgress = true;
        StatusText = "正在从服务器同步...";
        try
        {
            var synced = await Task.Run(async () => await _tokenSync.SyncFromServerAsync());
            var success = synced && await Task.Run(async () => await _auth.IsAuthenticatedAsync());
            if (success)
            {
                IsConnected = true;
                StatusText = "已认证";
                await (_toast?.ShowAsync("云文档同步成功") ?? Task.CompletedTask);
            }
            else
            {
                StatusText = "同步失败（服务器无有效sid）";
                await (_toast?.ShowAsync("同步失败，服务器无有效sid") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudDocsConnectorCard] 登录失败: {ex.Message}");
            StatusText = "操作失败";
            await (_toast?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsPrimaryInProgress = false;
        }
    }
}

/// <summary>mcphub 网关连接器卡（手机端从服务器拉 JWT——毓数 SQL 查询/权限工单的鉴权网关）。</summary>
public sealed partial class McphubConnectorCard : ConnectorCardViewModel
{
    private readonly IMcphubAuthService _auth;
    private readonly IToastService? _toast;

    public McphubConnectorCard(IMcphubAuthService auth, IToastService? toast)
    {
        _auth = auth;
        _toast = toast;
        Icon = "🛰️";
        IconBackground = "#F3E5F5";
        Title = "mcphub 网关";
        Description = "从服务器同步 JWT，网关鉴权毓数 SQL/权限工单工具";
        PrimaryButtonText = "从服务器同步";
        PrimaryCommand = new AsyncRelayCommand(LoginAsync);
    }

    public override async Task RefreshAsync()
    {
        try
        {
            var isAuth = await Task.Run(async () => await _auth.IsAuthenticatedAsync());
            IsConnected = isAuth;
            StatusText = isAuth ? "已认证" : "未认证";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[McphubConnectorCard] 检查认证状态失败: {ex.Message}");
            IsConnected = false;
            StatusText = "状态未知";
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsPrimaryInProgress) return;
        IsPrimaryInProgress = true;
        StatusText = "正在从服务器同步...";
        try
        {
            var token = await Task.Run(async () => await _auth.LoginAsync());
            if (token != null)
            {
                IsConnected = true;
                StatusText = "已认证";
                await (_toast?.ShowAsync("mcphub 同步成功") ?? Task.CompletedTask);
            }
            else
            {
                StatusText = "同步失败（服务器无有效凭据）";
                await (_toast?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[McphubConnectorCard] 登录失败: {ex.Message}");
            StatusText = "操作失败";
            await (_toast?.ShowAsync("操作失败，请重试") ?? Task.CompletedTask);
        }
        finally
        {
            IsPrimaryInProgress = false;
        }
    }
}
