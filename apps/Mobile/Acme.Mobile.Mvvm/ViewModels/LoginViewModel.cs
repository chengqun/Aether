using System;
using System.Threading.Tasks;
using Acme.AppAuth.Application.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class LoginViewModel : NavigableViewModelBase
{
    private readonly IAppAuthService? _authService;
    private readonly IPostLoginInitializer? _postLoginInitializer;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isLoginEnabled = true;

    [ObservableProperty]
    private bool _hasError;

    /// <summary>
    /// 登录成功后触发，平台层导航到初始化页
    /// </summary>
    public event EventHandler? LoginCompleted;

    /// <summary>
    /// 请求导航到注册页
    /// </summary>
    public event EventHandler? NavigateToRegisterRequested;

    public LoginViewModel()
    {
    }

    public LoginViewModel(IAppAuthService authService, IPostLoginInitializer? postLoginInitializer = null)
    {
        _authService = authService;
        _postLoginInitializer = postLoginInitializer;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "请输入用户名和密码";
            HasError = true;
            return;
        }

        IsLoading = true;
        IsLoginEnabled = false;
        StatusMessage = "正在登录...";
        HasError = false;

        try
        {
            if (_authService != null)
            {
                var response = await _authService.LoginAsync(Username, Password);
                if (response.Success)
                {
                    // 登录关键初始化(阻塞):应用本地缓存配置;首次登录缓存空时兜底拉一次 LLM。
                    // 由登录 spinner 吸收,完成后直接进 ChatPage。
                    StatusMessage = "正在加载配置...";
                    if (_postLoginInitializer != null)
                        await Task.Run(() => _postLoginInitializer.InitializeCriticalAsync());

                    StatusMessage = "登录成功";
                    HasError = false;
                    LoginCompleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    StatusMessage = response.Error ?? "用户名或密码错误";
                    HasError = true;
                }
            }
            else
            {
                await Task.Delay(1000);
                StatusMessage = "演示模式 - 登录成功";
                HasError = false;
                LoginCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"登录失败: {ex.Message}";
            HasError = true;
        }
        finally
        {
            IsLoading = false;
            IsLoginEnabled = true;
        }
    }

    [RelayCommand]
    private Task NavigateToRegisterAsync()
    {
        NavigateToRegisterRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
