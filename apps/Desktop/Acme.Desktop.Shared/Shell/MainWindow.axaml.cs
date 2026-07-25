using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌面 shell 主窗口（两租户共用）：左 200px 导航栏 + 右内容区。
/// DataContext = 租户 MainViewModel（: MainViewModelBase）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
