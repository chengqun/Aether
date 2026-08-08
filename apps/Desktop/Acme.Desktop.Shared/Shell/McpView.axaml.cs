using Acme.Shell;
using Aether.AI.Abstractions;
using Aether.AI.Hosting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class McpView : UserControl
{
    public McpView() => InitializeComponent();

    private McpViewModel? VM => DataContext as McpViewModel;
    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        await ShowEditDialogAsync(VM.CreateEditViewModel(null));
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is McpServerStatus status && VM != null)
            await ShowEditDialogAsync(VM.CreateEditViewModel(status));
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is McpServerStatus status && VM != null)
            VM.DeleteCommand.Execute(status);
    }

    private async System.Threading.Tasks.Task ShowEditDialogAsync(McpServerEditViewModel vm)
    {
        if (VM == null) return;
        try
        {
            var window = new McpServerEditWindow { DataContext = vm };
            var owner = GetOwnerWindow();

            vm.SaveRequested += async config =>
            {
                await VM.CommitEditAsync(config);
                window.Close();
            };
            vm.CancelRequested += () => window.Close();

            if (owner != null) await window.ShowDialog(owner);
            else window.Show();
        }
        catch { /* 打开失败忽略 */ }
    }
}
