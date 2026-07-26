using Acme.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

/// <summary>登录对话框。登录成功由 <see cref="LoginViewModel.LoggedIn"/> 触发关闭；取消按钮关闭。</summary>
public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
