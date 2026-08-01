using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

/// <summary>提问/确认对话框。回答由 <see cref="AskUserDialogViewModel.Finished"/> 触发关闭。</summary>
public partial class AskUserWindow : Window
{
    public AskUserWindow()
    {
        InitializeComponent();
    }
}
