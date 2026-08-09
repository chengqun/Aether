using System;
using System.Threading.Tasks;
using Acme.AppAuth.Application.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class RegisterViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService? _authService;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _emailAddress = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _confirmPassword = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isRegisterEnabled = true;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>
    /// 注册成功后触发，平台层导航到聊天页
    /// </summary>
    public event EventHandler? RegistrationCompleted;

    /// <summary>
    /// 请求导航回登录页
    /// </summary>
    public event EventHandler? NavigateToLoginRequested;

    public RegisterViewModel()
    {
    }

    public RegisterViewModel(IAppAuthService authService)
    {
        _authService = authService;
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(EmailAddress)
            || string.IsNullOrWhiteSpace(Password) || string.IsNullOrWhiteSpace(ConfirmPassword))
        {
            StatusMessage = "请填写所有字段";
            HasError = true;
            return;
        }

        if (Password != ConfirmPassword)
        {
            StatusMessage = "两次输入的密码不一致";
            HasError = true;
            return;
        }

        IsLoading = true;
        IsRegisterEnabled = false;
        StatusMessage = "正在注册...";
        HasError = false;

        try
        {
            if (_authService != null)
            {
                // Aether 的 IAppAuthService 仅暴露登录/刷新/登出（与桌面端一致），不含注册契约。
                // 注册引导用户回到登录页（可通过网页端注册后用已有账号登录）。
                StatusMessage = "注册请通过网页端完成，再使用已有账号登录";
                HasError = false;
                await Task.Delay(800);
                NavigateToLoginRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                await Task.Delay(1000);
                StatusMessage = "演示模式 - 注册成功";
                HasError = false;
                RegistrationCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"注册失败: {ex.Message}";
            HasError = true;
        }
        finally
        {
            IsLoading = false;
            IsRegisterEnabled = true;
        }
    }

    [RelayCommand]
    private Task GoToLoginAsync()
    {
        NavigateToLoginRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
