using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class WorkflowsView : UserControl
{
    public WorkflowsView() { InitializeComponent(); }

    private WorkflowsViewModel? VM => DataContext as WorkflowsViewModel;
    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    private async void OnAddClick(object? sender, RoutedEventArgs e) => await EditAsync(null);

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WorkflowItem item) await EditAsync(item);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WorkflowItem item && VM != null)
            VM.DeleteCommand.Execute(item);
    }

    private void OnToggleSummonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WorkflowItem item && VM != null)
            VM.ToggleSummonCommand.Execute(item);
    }

    private async Task EditAsync(WorkflowItem? existing)
    {
        if (VM == null) return;
        try
        {
            var vm = await VM.CreateEditViewModelAsync(existing);
            var window = new WorkflowEditWindow { DataContext = vm };
            var owner = GetOwnerWindow();

            vm.SaveRequested += async saved =>
            {
                var result = await VM.SaveAsync(saved);
                if (result.Success) window.Close();
                else vm.Error = result.Error;
            };
            vm.AutoSaveRequested += async saved =>
            {
                // 自动保存不关弹窗；失败回写错误
                var result = await VM.SaveAsync(saved);
                if (!result.Success) vm.Error = result.Error;
            };
            vm.CancelRequested += () => window.Close();

            if (owner != null) await window.ShowDialog(owner);
            else window.Show();
        }
        catch (Exception ex) { VM.StatusMessage = $"打开编辑窗口失败：{ex.Message}"; }
    }
}
