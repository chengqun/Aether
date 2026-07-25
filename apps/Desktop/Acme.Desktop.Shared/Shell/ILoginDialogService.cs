using Acme.AppAuth.Application.Auth;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Acme.Desktop.Shared.Shell;

/// <summary>弹出登录对话框，返回是否登录成功。</summary>
public interface ILoginDialogService
{
    Task<bool> ShowLoginAsync();
}

/// <summary>
/// 默认实现：从 <see cref="Application.Current"/>.ApplicationLifetime 取主窗口，
/// 把 <see cref="LoginWindow"/> 作为模态对话框弹出。供两租户共用，无需各租户再实现。
/// </summary>
public sealed class LoginDialogService : ILoginDialogService
{
    private readonly IAppAuthService _auth;

    public LoginDialogService(IAppAuthService auth) => _auth = auth;

    public async Task<bool> ShowLoginAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return false;
        }

        var vm = new LoginViewModel(_auth);
        var window = new LoginWindow { DataContext = vm };
        vm.LoggedIn += window.Close;
        await window.ShowDialog(desktop.MainWindow);
        return vm.LoginSucceededFlag;
    }
}
