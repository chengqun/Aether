using System;
using Acme.Shell;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class WorkflowEditView : UserControl
{
    private Expander[]? _accordions;

    public WorkflowEditView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        _accordions = new[]
        {
            this.FindControl<Expander>("ExpBasic")!,
            this.FindControl<Expander>("ExpParticipants")!,
            this.FindControl<Expander>("ExpHandoffs")!,
        };
        foreach (var exp in _accordions)
        {
            exp.PropertyChanged += (s, args) =>
            {
                if (args.Property == Expander.IsExpandedProperty && (bool)args.NewValue!)
                    foreach (var other in _accordions!)
                        if (!ReferenceEquals(other, s)) other.IsExpanded = false;
            };
        }
    }

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
