using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Acme.DeviceConnect.Domain;
using Acme.DeviceConnect.Infrastructure;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private ILlmConfigService? _llmService;
    private IDeviceChatClient? _deviceChatClient;
    private DeviceConfig? _deviceConfig;
    private SqliteDeviceConfigRepository? _deviceConfigRepo;

    private ILlmConfigService LlmService => _llmService ??= _serviceProvider.GetRequiredService<ILlmConfigService>();
    private IDeviceChatClient DeviceChatClient => _deviceChatClient ??= _serviceProvider.GetRequiredService<IDeviceChatClient>();
    private DeviceConfig DeviceConfig => _deviceConfig ??= _serviceProvider.GetRequiredService<DeviceConfig>();
    private SqliteDeviceConfigRepository DeviceConfigRepo => _deviceConfigRepo ??= _serviceProvider.GetRequiredService<SqliteDeviceConfigRepository>();

    public SettingsViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [ObservableProperty]
    private string _activeTab = "llm";

    public bool IsLlmTabActive => ActiveTab == "llm";
    public bool IsDeviceTabActive => ActiveTab == "device";
    public bool IsNotLlmTab => ActiveTab != "llm";
    public bool IsNotDeviceTab => ActiveTab != "device";

    [ObservableProperty]
    private ObservableCollection<LlmConfigDto> _llmConfigs = new();

    [ObservableProperty]
    private string? _llmError;

    [ObservableProperty]
    private bool _isLoadingLlm;

    public bool HasLlmError => !string.IsNullOrEmpty(LlmError);
    public bool HasNoLlmConfigs => LlmConfigs.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private string _deviceServerUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private string _deviceAuthorizationCode = "";

    [ObservableProperty]
    private string _deviceName = "桌面助手";

    [ObservableProperty]
    private string _deviceType = "desktop";

    [ObservableProperty]
    private string _deviceTenant = "default";

    [ObservableProperty]
    private bool _autoConnect;

    [ObservableProperty]
    private string? _deviceError;

    [ObservableProperty]
    private string _connectionStatusText = "未连接";

    private IBrush? _connectionStatusColor;
    public IBrush ConnectionStatusColor
    {
        get => _connectionStatusColor ?? new SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 0));
        private set
        {
            _connectionStatusColor = value;
            OnPropertyChanged();
        }
    }

    public bool IsDeviceConnected => DeviceChatClient.IsConnected;

    public bool CanConnect => !IsDeviceConnected && 
                              !string.IsNullOrWhiteSpace(DeviceServerUrl) && 
                              !string.IsNullOrWhiteSpace(DeviceAuthorizationCode);

    public bool HasDeviceError => !string.IsNullOrEmpty(DeviceError);

    public event Action? SettingsSaved;

    public async Task LoadAsync()
    {
        await LoadLlmConfigsAsync();
        await LoadDeviceConfigAsync();
        UpdateConnectionStatus();
        SubscribeToConnectionStateChanges();
    }

    private async Task LoadLlmConfigsAsync()
    {
        IsLoadingLlm = true;
        LlmError = null;
        try
        {
            var configs = await LlmService.GetCachedAsync();
            LlmConfigs.Clear();
            if (configs != null)
            {
                foreach (var config in configs)
                    LlmConfigs.Add(config);
            }
        }
        catch (Exception ex)
        {
            LlmError = ex.Message;
        }
        finally
        {
            IsLoadingLlm = false;
        }
    }

    private async Task LoadDeviceConfigAsync()
    {
        try
        {
            var cacheItem = await DeviceConfigRepo.GetByTenantAsync(DeviceConfig.Tenant);
            if (cacheItem != null)
            {
                DeviceServerUrl = cacheItem.ServerUrl;
                DeviceAuthorizationCode = cacheItem.AuthorizationCode;
                DeviceName = cacheItem.DeviceName;
                DeviceType = cacheItem.DeviceType;
                DeviceTenant = cacheItem.Tenant;
                AutoConnect = cacheItem.AutoConnect;
            }
            else
            {
                DeviceServerUrl = DeviceConfig.ServerUrl;
                DeviceAuthorizationCode = DeviceConfig.AuthorizationCode;
                DeviceName = DeviceConfig.DeviceName;
                DeviceType = DeviceConfig.DeviceType;
                DeviceTenant = DeviceConfig.Tenant;
            }
        }
        catch (Exception ex)
        {
            DeviceError = ex.Message;
        }
    }

    private async Task SaveDeviceConfigAsync()
    {
        try
        {
            var cacheItem = await DeviceConfigRepo.GetByTenantAsync(DeviceTenant);
            if (cacheItem == null)
            {
                cacheItem = new DeviceConfigCacheItem
                {
                    Tenant = DeviceTenant
                };
            }
            cacheItem.ServerUrl = DeviceServerUrl;
            cacheItem.AuthorizationCode = DeviceAuthorizationCode;
            cacheItem.DeviceName = DeviceName;
            cacheItem.DeviceType = DeviceType;
            cacheItem.AutoConnect = AutoConnect;
            cacheItem.CachedAt = DateTimeOffset.UtcNow;
            await DeviceConfigRepo.UpsertAsync(cacheItem);

            var cfg = DeviceConfig;
            cfg.ServerUrl = DeviceServerUrl;
            cfg.AuthorizationCode = DeviceAuthorizationCode;
            cfg.DeviceName = DeviceName;
            cfg.DeviceType = DeviceType;
            cfg.Tenant = DeviceTenant;
        }
        catch (Exception ex)
        {
            DeviceError = ex.Message;
        }
    }

    private void SubscribeToConnectionStateChanges()
    {
        DeviceChatClient.ConnectionStateChanged += status =>
        {
            ConnectionStatusText = status;
            if (status.Contains("成功") || status.Contains("已连接"))
            {
                ConnectionStatusColor = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0, 128, 0));
            }
            else if (status.Contains("断开") || status.Contains("失败"))
            {
                ConnectionStatusColor = new SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 0));
            }
            else
            {
                ConnectionStatusColor = new SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 165, 0));
            }
            OnPropertyChanged(nameof(IsDeviceConnected));
            OnPropertyChanged(nameof(CanConnect));
        };
    }

    private void UpdateConnectionStatus()
    {
        if (DeviceChatClient.IsConnected)
        {
            ConnectionStatusText = "已连接";
            ConnectionStatusColor = new SolidColorBrush(Avalonia.Media.Color.FromRgb(0, 128, 0));
        }
        else
        {
            ConnectionStatusText = "未连接";
            ConnectionStatusColor = new SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 0));
        }
        OnPropertyChanged(nameof(IsDeviceConnected));
        OnPropertyChanged(nameof(CanConnect));
    }

    [RelayCommand]
    private void ShowAddLlmModal()
    {
        ShowLlmEditModal(null);
    }

    [RelayCommand]
    private void EditLlmConfig(LlmConfigDto config)
    {
        ShowLlmEditModal(config);
    }

    private async void ShowLlmEditModal(LlmConfigDto? config)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return;
        }

        var vm = new LlmConfigEditViewModel(config);
        var window = new LlmConfigEditWindow { DataContext = vm };

        vm.SaveRequested += async (savedConfig) =>
        {
            try
            {
                await LlmService.SetConfigAsync(savedConfig);
                await LoadLlmConfigsAsync();
            }
            catch (Exception ex)
            {
                vm.Error = ex.Message;
                return;
            }

            window.Close();
        };
        vm.CancelRequested += () =>
        {
            window.Close();
        };

        await window.ShowDialog(desktop.MainWindow);
    }

    [RelayCommand]
    private async Task DeleteLlmConfigAsync(LlmConfigDto config)
    {
        if (config.IsSystem) return;

        LlmError = null;
        try
        {
            var index = LlmConfigs.IndexOf(config);
            LlmConfigs.Remove(config);

            if (LlmConfigs.Count > 0)
            {
                var newDefault = LlmConfigs.FirstOrDefault(c => c.IsDefault) ?? LlmConfigs[0];
                if (!newDefault.IsDefault)
                {
                    newDefault.IsDefault = true;
                    await LlmService.SetConfigAsync(newDefault);
                }
            }
        }
        catch (Exception ex)
        {
            LlmError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SetDefaultLlmConfigAsync(LlmConfigDto config)
    {
        LlmError = null;
        try
        {
            foreach (var c in LlmConfigs)
                c.IsDefault = false;
            config.IsDefault = true;
            await LlmService.SetConfigAsync(config);
            await LoadLlmConfigsAsync();
        }
        catch (Exception ex)
        {
            LlmError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ConnectDeviceAsync()
    {
        DeviceError = null;

        if (string.IsNullOrWhiteSpace(DeviceServerUrl))
        {
            DeviceError = "服务器地址不能为空";
            return;
        }

        if (string.IsNullOrWhiteSpace(DeviceAuthorizationCode))
        {
            DeviceError = "设备认证码不能为空";
            return;
        }

        try
        {
            _ = new Uri(DeviceServerUrl);
        }
        catch
        {
            DeviceError = "服务器地址格式不正确";
            return;
        }

        try
        {
            await DeviceChatClient.ConnectAsync(
                DeviceServerUrl,
                DeviceAuthorizationCode,
                DeviceName,
                DeviceType,
                DeviceTenant);

            await SaveDeviceConfigAsync();
            UpdateConnectionStatus();

            // 直接通知主 ViewModel 刷新连接状态（不依赖事件链）
            try
            {
                _serviceProvider.GetRequiredService<MainViewModelBase>().RefreshConnectionState();
            }
            catch { }

            SettingsSaved?.Invoke();
        }
        catch (Exception ex)
        {
            DeviceError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync()
    {
        DeviceError = null;
        try
        {
            await DeviceChatClient.DisconnectAsync();
            UpdateConnectionStatus();

            // 直接通知主 ViewModel 刷新连接状态
            try
            {
                _serviceProvider.GetRequiredService<MainViewModelBase>().RefreshConnectionState();
            }
            catch { }
        }
        catch (Exception ex)
        {
            DeviceError = ex.Message;
        }
    }

    [RelayCommand]
    private void SwitchTab(string tab)
    {
        ActiveTab = tab;
        OnPropertyChanged(nameof(IsLlmTabActive));
        OnPropertyChanged(nameof(IsDeviceTabActive));
        OnPropertyChanged(nameof(IsNotLlmTab));
        OnPropertyChanged(nameof(IsNotDeviceTab));
    }

    public async Task AutoConnectIfEnabledAsync()
    {
        if (!AutoConnect) return;
        if (string.IsNullOrWhiteSpace(DeviceServerUrl) || string.IsNullOrWhiteSpace(DeviceAuthorizationCode)) return;
        
        try
        {
            await DeviceChatClient.ConnectAsync(
                DeviceServerUrl,
                DeviceAuthorizationCode,
                DeviceName,
                DeviceType,
                DeviceTenant);
            UpdateConnectionStatus();
        }
        catch
        {
        }
    }
}