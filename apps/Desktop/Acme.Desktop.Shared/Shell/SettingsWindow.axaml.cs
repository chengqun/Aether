using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

public partial class SettingsWindow : ThemedWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
