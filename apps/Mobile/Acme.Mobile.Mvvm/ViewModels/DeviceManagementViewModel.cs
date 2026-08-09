using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.DeviceManagement.Domain;
using Acme.Mobile.Mvvm.Abstractions;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 设备管理 ViewModel：显示已绑定的设备列表，支持刷新、新增设备、复制授权码、恢复联系人。
/// </summary>
public partial class DeviceManagementViewModel : NavigableViewModelBase
{
    private readonly IDeviceService? _deviceService;
    private readonly IClipboardService? _clipboardService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isAdding;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _newDeviceName = string.Empty;

    [ObservableProperty]
    private string _newDeviceType = "Console";

    [ObservableProperty]
    private bool _hasDevices;

    /// <summary>已绑定的设备列表</summary>
    public ObservableCollection<MyDevice> Devices { get; } = new();

    public DeviceManagementViewModel(IDeviceService? deviceService = null,
                                     IClipboardService? clipboardService = null,
                                     IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _deviceService = deviceService;
        _clipboardService = clipboardService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        await LoadDevicesAsync();
    }

    [RelayCommand]
    public async Task LoadDevicesAsync()
    {
        if (_deviceService == null)
        {
            StatusMessage = "服务未初始化";
            return;
        }

        IsLoading = true;
        StatusMessage = string.Empty;

        try
        {
            // GetMyDevicesAsync 返回 nullable：null=HTTP 失败（不清空既有列表，保留可见数据），空列表=成功无设备。
            var devices = await Task.Run(async () => await _deviceService.GetMyDevicesAsync());

            if (devices != null)
            {
                Devices.Clear();
                foreach (var device in devices)
                {
                    Devices.Add(device);
                }
                HasDevices = Devices.Count > 0;
                if (!HasDevices)
                    StatusMessage = "暂无绑定设备";
            }
            else
            {
                // 失败：不清空 Devices、不动 HasDevices（保留既有列表可见），仅提示失败。
                StatusMessage = "加载设备列表失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
            HasDevices = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddDeviceAsync()
    {
        if (_deviceService == null) return;

        if (string.IsNullOrWhiteSpace(NewDeviceName))
        {
            StatusMessage = "请输入设备名称";
            return;
        }

        IsAdding = true;
        StatusMessage = string.Empty;

        try
        {
            // CreateDeviceAsync 失败抛 HttpRequestException（不兜底空对象），由 catch 处理 → "创建失败"。
            await Task.Run(async () =>
                await _deviceService.CreateDeviceAsync(NewDeviceName, NewDeviceType));

            StatusMessage = "设备创建成功";
            NewDeviceName = string.Empty;
            NewDeviceType = "Console";
            // 重新加载列表
            await LoadDevicesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建失败: {ex.Message}";
        }
        finally
        {
            IsAdding = false;
        }
    }

    [RelayCommand]
    private async Task CopyAuthCodeAsync(string? authCode)
    {
        if (string.IsNullOrEmpty(authCode)) return;

        try
        {
            if (_clipboardService != null)
            {
                await _clipboardService.SetTextAsync(authCode);
                StatusMessage = "授权码已复制到剪贴板";
            }
            else
            {
                StatusMessage = "剪贴板服务不可用";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task RestoreContactAsync(string? deviceId)
    {
        // 恢复联系人功能需要后端支持，暂时提示
        StatusMessage = "恢复联系人功能开发中...";
        return Task.CompletedTask;
    }
}
