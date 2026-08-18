using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>浏览器网络面板视图（DevTools Network 风格）：会话标签条 + 地址栏 + 请求列表 + 请求头详情。
/// DataContext = <see cref="BrowserViewModel"/>（由 BottomPanelView 经 DesktopApp.Services 定位器解析注入）。
/// 列表项点击 → <see cref="BrowserViewModel.SelectedRequest"/>（详情显 headers）；标签点击 → 切会话；✕ → 关会话。</summary>
public partial class BrowserView : UserControl
{
    public BrowserView() => InitializeComponent();

    /// <summary>地址栏回车 → 触发 Navigate。</summary>
    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BrowserViewModel vm)
        {
            vm.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>点会话标签 → 切活跃会话（标签的 DataContext = BrowserSessionItem）。</summary>
    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.SwitchSessionCommand.Execute(item);
    }

    /// <summary>点标签 ✕ → 关闭该会话。</summary>
    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is BrowserSessionItem item && DataContext is BrowserViewModel vm)
            vm.CloseSessionCommand.Execute(item);
    }
}
