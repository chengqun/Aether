using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class ExpertsView : UserControl
{
    public ExpertsView()
    {
        InitializeComponent();
    }

    private ExpertsViewModel? VM => DataContext as ExpertsViewModel;

    private void OnAddExpertClick(object sender, RoutedEventArgs e)
        => VM?.AddExpertCommand.Execute(null);

    private void OnSaveExpertClick(object sender, RoutedEventArgs e)
        => VM?.SaveExpertCommand.Execute(null);

    private void OnCancelEditClick(object sender, RoutedEventArgs e)
        => VM?.CancelEditCommand.Execute(null);

    private void OnToggleSummonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
            VM.ToggleSummonCommand.Execute(item);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
            VM.EditExpertCommand.Execute(item);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ExpertItem item && VM != null)
            VM.DeleteExpertCommand.Execute(item);
    }
}
