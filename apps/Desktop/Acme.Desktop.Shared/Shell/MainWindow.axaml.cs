using Acme.Shell;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌面 shell 主窗口（两租户共用）。展开/收起两套侧边栏在 XAML 里靠 IsVisible 切换，
/// 无业务逻辑。DataContext = 租户 MainViewModel（: MainViewModelBase）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
