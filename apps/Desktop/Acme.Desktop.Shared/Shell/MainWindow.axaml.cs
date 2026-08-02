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
    /// <summary>侧边栏「最近会话」滚到底部附近时触发增量加载的阈值（逻辑像素）。</summary>
    private const double ConversationsLoadThreshold = 80;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>侧边栏会话列表滚到底部附近时，增量加载更早的会话条目。</summary>
    private void OnConversationsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainViewModelBase vm) return;
        if (!vm.HasMoreConversations) return;
        double distFromBottom = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
        if (distFromBottom < ConversationsLoadThreshold)
            vm.LoadMoreConversations();
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
