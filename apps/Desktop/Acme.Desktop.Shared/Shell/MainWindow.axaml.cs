using Acme.Shell;
using Avalonia.Controls;
using Avalonia.Interactivity;

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


    /// <summary>打开运行 Trace 查看窗口（OTel agent.run span）。</summary>
    public void OnOpenTrace(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModelBase vm && vm.Trace is { } trace)
        {
            var window = new TraceWindow { DataContext = trace };
            window.Show();
        }
    }
}
