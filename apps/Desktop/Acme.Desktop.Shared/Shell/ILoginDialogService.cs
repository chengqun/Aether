using Acme.AppAuth.Application.Auth;
using Acme.Shell;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="ILoginDialogService"/> 的 Avalonia 实现：从 Application.Current 取主窗口，
/// 把 LoginWindow 作为模态对话框弹出。供两租户共用。
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
