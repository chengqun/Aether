using Acme.Desktop.Shared.Shell;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Qifu.Desktop.Views;

public partial class MySqlSettingsWindow : ThemedWindow
{
    public MySqlSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
