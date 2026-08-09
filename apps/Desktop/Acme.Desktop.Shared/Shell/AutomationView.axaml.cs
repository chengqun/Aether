using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class AutomationView : UserControl
{
    public AutomationView()
    {
        InitializeComponent();
    }

    private AutomationViewModel? VM => DataContext as AutomationViewModel;

    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    private async void OnAddTaskClick(object? sender, RoutedEventArgs e)
    {
        if (VM == null) return;
        var row = VM.CreateNewRow();
        await ShowDialogAsync(row);
    }

    private async void OnEditClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AutomationTaskRow item && VM != null)
        {
            var row = VM.CreateEditRow(item);
            await ShowDialogAsync(row);
        }
    }

    private async Task ShowDialogAsync(AutomationTaskRow row)
    {
        if (VM == null) return;
        var owner = GetOwnerWindow();
        if (owner == null) return;

        var dialog = new AutomationEditWindow { DataContext = row };
        var saved = owner == null ? false : await dialog.ShowDialog<bool>(owner);
        if (saved)
            await VM.SaveTaskCommand.ExecuteAsync(row);
    }

    private void OnRunNowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AutomationTaskRow item && VM != null)
            VM.RunNowCommand.Execute(item);
    }

    private void OnToggleEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AutomationTaskRow item && VM != null)
            VM.ToggleEnabledCommand.Execute(item);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is AutomationTaskRow item && VM != null)
            VM.DeleteTaskCommand.Execute(item);
    }
}
