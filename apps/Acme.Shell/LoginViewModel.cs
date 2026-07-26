using Acme.AppAuth.Application.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// 登录对话框 ViewModel：用户名/密码 → <see cref="IAppAuthService.LoginAsync"/>。
/// 成功置 <see cref="LoginSucceededFlag"/> 并触发 <see cref="LoggedIn"/>（LoginWindow 据此关闭）。
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IAppAuthService _auth;

    public LoginViewModel(IAppAuthService auth) => _auth = auth;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _username = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _password = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isBusy;

    /// <summary>登录是否成功（LoginDialogService 在窗口关闭后读取）。</summary>
    public bool LoginSucceededFlag { get; private set; }

    /// <summary>登录成功时触发，LoginWindow 订阅后关闭自身。</summary>
    public event Action? LoggedIn;

    private bool CanLogin() => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var result = await _auth.LoginAsync(Username, Password);
            if (result.Success)
            {
                LoginSucceededFlag = true;
                LoggedIn?.Invoke();
            }
            else
            {
                Error = result.Error ?? "登录失败";
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
