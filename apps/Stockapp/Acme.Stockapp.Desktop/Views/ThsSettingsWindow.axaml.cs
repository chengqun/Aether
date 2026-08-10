using Acme.Desktop.Shared.Shell;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Stockapp.Desktop.Views;

public partial class ThsSettingsWindow : ThemedWindow
{
    public ThsSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
