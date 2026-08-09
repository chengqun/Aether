using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Services;
using Acme.AppUpdate.Domain;
using Acme.AppUpdate.Domain;
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class MeViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService? _authService;
    private readonly IAppTokenStore? _tokenStore;
    private readonly IThemeManager? _themeManager;
    private readonly IPlatformInfo? _platformInfo;
    private readonly IUpdateService? _updateService;
    private readonly ISignalRChatService? _signalRChatService;

    [ObservableProperty]
    private string _username = "未登录";

    [ObservableProperty]
    private string _usernameInitial = "?";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isAdmin;

    [ObservableProperty]
    private ThemeMode _selectedThemeMode;

    [ObservableProperty]
    private bool _isDarkMode;

    [ObservableProperty]
    private string _versionInfo = string.Empty;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private CheckUpdateResult? _latestUpdateInfo;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>
    /// 检查更新结果事件，平台层可弹出更新对话框
    /// </summary>
    public event EventHandler<CheckUpdateResult>? UpdateChecked;

    public string ThemeModeText => IsDarkMode ? "深色模式" : "浅色模式";

    public string ThemeIcon => IsDarkMode ? "🌙" : "☀";

    partial void OnIsDarkModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ThemeModeText));
        OnPropertyChanged(nameof(ThemeIcon));
    }

    public MeViewModel(IAppAuthService? authService = null,
                       IThemeManager? themeManager = null,
                       IPlatformInfo? platformInfo = null,
                       IUpdateService? updateService = null,
                       IUIThreadDispatcher? uiThreadDispatcher = null,
                       ISignalRChatService? signalRChatService = null,
                       IAppTokenStore? tokenStore = null)
    {
        _authService = authService;
        _tokenStore = tokenStore;
        _themeManager = themeManager;
        _platformInfo = platformInfo;
        _updateService = updateService;
        _uiThreadDispatcher = uiThreadDispatcher;
        _signalRChatService = signalRChatService;
    }

    [RelayCommand]
    public async Task LoadDataAsync()
    {
        await LoadThemeModeAsync();
        await LoadUserInfoAsync();
        await CheckAdminAsync();
        LoadVersionInfo();
    }

    private void LoadVersionInfo()
    {
        if (_platformInfo != null)
        {
            var versionStr = _platformInfo.GetVersionString();
            var platformName = _platformInfo.GetAppPlatform().ToString();
            VersionInfo = $"版本 {versionStr} ({platformName})";
        }
        else
        {
            VersionInfo = "版本未知";
        }
    }

    private async Task LoadThemeModeAsync()
    {
        if (_themeManager != null)
        {
            var themeMode = await Task.Run(async () => await _themeManager.GetSavedThemeAsync());
            SelectedThemeMode = themeMode;
            IsDarkMode = SelectedThemeMode == ThemeMode.Dark;
        }
    }

    private async Task LoadUserInfoAsync()
    {
        IsLoading = true;
        try
        {
            if (_tokenStore != null)
            {
                var username = await Task.Run(() => _tokenStore.GetUsernameAsync());

                Username = !string.IsNullOrEmpty(username) ? username : "未登录";
                UsernameInitial = !string.IsNullOrEmpty(username) && username.Length > 0
                    ? username.Trim().ToUpper().FirstOrDefault().ToString()
                    : "?";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MeViewModel] Error loading user info: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CheckAdminAsync()
    {
        // IsAdminAsync 在 Aether IAppAuthService 中不存在，移动端 admin 门控未使用，暂置 false
        await Task.CompletedTask;
        IsAdmin = false;
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        IsLoading = true;
        try
        {
            // 先断开 SignalR 连接：SignalRChatService 是 Singleton，HubConnection 不会随登出自动释放，
            // 不断开会导致换用户登录时复用旧凭据的连接（收旧用户消息/串号）。
            if (_signalRChatService != null)
            {
                await Task.Run(() => _signalRChatService.DisconnectAsync());
            }

            if (_authService != null)
            {
                await Task.Run(() => _authService.LogoutAsync());
            }

            Username = "未登录";
            UsernameInitial = "?";

            NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("LoginPage", true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MeViewModel] Error during logout: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task NavigateToDeviceManagementAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("DeviceManagementPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NavigateToAiSettingsAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("AiSettingsPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NavigateToLlmConfigAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("LlmConfigPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NavigateToCreateGroupAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("CreateGroupPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NavigateToMarketAsync()
    {
        // Agent 市场：浏览云端 agent 目录并召唤到本地（IAgentMarketplaceService）。
        // 原在「发现」页，按需移至此处——与 AI设置/LLM配置 并列，属 agent 获取入口。
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("MarketPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (_updateService == null || _platformInfo == null)
        {
            UpdateStatusMessage = "更新服务不可用";
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatusMessage = "正在检查更新...";

        try
        {
            var platform = _platformInfo.GetAppPlatform();
            var versionCode = _platformInfo.GetVersionCode();

            // CheckUpdateAsync 返回裸 CheckUpdateResult（无 .Success/.Data/.Error）
            var result = await Task.Run(() => _updateService.CheckUpdateAsync(platform, versionCode));

            if (result != null && result.HasUpdate)
            {
                LatestUpdateInfo = result;
                UpdateStatusMessage = $"发现新版本: {result.VersionName}";
                UpdateChecked?.Invoke(this, result);
            }
            else if (result != null)
            {
                UpdateStatusMessage = "已是最新版本";
            }
            else
            {
                UpdateStatusMessage = "检查更新失败";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"检查更新失败: {ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task SetThemeAsync(string? themeModeStr)
    {
        if (_themeManager == null || string.IsNullOrEmpty(themeModeStr)) return;

        if (Enum.TryParse<ThemeMode>(themeModeStr, out var themeMode))
        {
            await _themeManager.SetThemeAsync(themeMode);
            SelectedThemeMode = themeMode;
            IsDarkMode = themeMode == ThemeMode.Dark;
        }
    }

    public async Task ToggleThemeAsync(bool isDark)
    {
        try
        {
            if (_themeManager != null)
            {
                var themeMode = isDark ? ThemeMode.Dark : ThemeMode.Light;
                await _themeManager.SetThemeAsync(themeMode);
                SelectedThemeMode = themeMode;
                IsDarkMode = isDark;
            }
        }
        catch
        {
        }
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await LoadDataAsync();
    }
}