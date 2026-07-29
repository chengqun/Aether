using Acme.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Acme.Desktop.Shared.Shell;

public partial class WorkflowEditWindow : Window
{
    public WorkflowEditWindow() { InitializeComponent(); }

    private void OnAddHandoffClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkflowEditViewModel vm) vm.AddHandoffCommand.Execute(null);
    }

    private void OnRemoveHandoffClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HandoffRow row && DataContext is WorkflowEditViewModel vm)
            vm.RemoveHandoffCommand.Execute(row);
    }
}
