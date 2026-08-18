using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>底部复合面板：终端 + 浏览器网络 tab 切换。注册为 PanelKeys.Terminal 的视图（DataContext=TerminalViewModel）。
/// 内部自管 tab 状态——不依赖 workspace SelectedTabIndex（主 tabbed 与底部 tab 互不干扰）。
/// BrowserPane 的 BrowserViewModel 经 <see cref="DesktopApp.Services"/> 定位器解析（同 NotesEditorView 范式）。
/// 折叠复用终端 region toggle（_terminalHost=本 view 的 ContentControl 宿主）。</summary>
public partial class BottomPanelView : UserControl
{
    public BottomPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => TerminalPane.DataContext = DataContext;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 本 view 的 DataContext = TerminalViewModel（vmFactory 注入）；BrowserPane 的 VM 经定位器解析。
        try { BrowserPane.DataContext = DesktopApp.Services?.GetRequiredService<MainViewModelBase>().Browser; }
        catch { /* 设计期 / 容器未就绪：静默 */ }
    }

    private void OnTerminalTab(object? sender, RoutedEventArgs e) => SwitchTab(0);
    private void OnBrowserTab(object? sender, RoutedEventArgs e) => SwitchTab(1);

    private void SwitchTab(int i)
    {
        TerminalPane.IsVisible = i == 0;
        BrowserPane.IsVisible = i == 1;
        TabTerminalBtn.Classes.Set("active", i == 0);
        TabBrowserBtn.Classes.Set("active", i == 1);
    }
}
