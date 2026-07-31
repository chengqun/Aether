using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

/// <summary>工具审批对话框。决定由 <see cref="ApprovalDialogViewModel.Finished"/> 触发关闭。</summary>
public partial class ApprovalWindow : Window
{
    public ApprovalWindow()
    {
        InitializeComponent();
    }
}
