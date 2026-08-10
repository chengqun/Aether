using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

public partial class LlmConfigEditWindow : ThemedWindow
{
    public LlmConfigEditWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}