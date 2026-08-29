using System;
using System.Threading.Tasks;
using Acme.AppAuth.Application.Auth;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 「设置」二级页 VM（我 → 设置）。退出登录藏于此页（主流 App 惯例：退出入口深一层的设置页，避免主界面误触）。
/// 登出流程镜像 <see cref="MeViewModel.LogoutAsync"/>：先断开 SignalR，再清 auth，最后回登录页。
/// </summary>
public partial class SettingsViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService? _authService;
    private readonly ISignalRChatService? _signalRChatService;

    [ObservableProperty] private bool _isLoading;

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public SettingsViewModel(IAppAuthService? authService = null, ISignalRChatService? signalRChatService = null)
    {
        _authService = authService;
        _signalRChatService = signalRChatService;
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            // 先断开 SignalR（Singleton，不随登出自动释放，避免换用户串号）。
            if (_signalRChatService != null)
                await Task.Run(() => _signalRChatService.DisconnectAsync());

            if (_authService != null)
                await Task.Run(() => _authService.LogoutAsync());

            NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("LoginPage", true));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsViewModel] 退出失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
