using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 「钱包」页视图：链上钱包的用户操作面（创建/导入种子、余额、转账、充值、提币）。
/// VM = <see cref="WalletViewModel"/>（MainViewModelBase.Wallet 共享单例，经 ViewRegistry 注入 DataContext）。
/// 复制地址走 code-behind（Clipboard 是 Avalonia 面）。
/// </summary>
public partial class WalletView : UserControl
{
    public WalletView()
    {
        InitializeComponent();
    }

    /// <summary>复制钱包地址到剪贴板（40 位 hex——对外收款的公开身份）。ClipboardExtensions 同 ReportWindow 范式。</summary>
    private async void OnCopyAddress(object? sender, RoutedEventArgs args)
    {
        if (DataContext is WalletViewModel vm && vm.Address is { } address
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try { await ClipboardExtensions.SetTextAsync(clipboard, address); } catch { }
        }
    }
}
