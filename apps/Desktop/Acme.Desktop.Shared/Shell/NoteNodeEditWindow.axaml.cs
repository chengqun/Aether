using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

public partial class NoteNodeEditWindow : Window
{
    public NoteNodeEditWindow()
    {
        InitializeComponent();
    }

    public void SetHeader(string text) => HeaderTitle.Text = text;

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}