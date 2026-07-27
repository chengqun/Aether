using System.Collections.ObjectModel;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

public partial class NoteNodeEditRow : ObservableObject
{
    public ObservableCollection<NodeTypeOption> TypeOptions { get; } = new()
    {
        new("Markdown 文本", NoteNodeType.Note),
        new("Agent", NoteNodeType.Agent),
        new("Workflow", NoteNodeType.Workflow),
    };

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private NodeTypeOption? _selectedTypeOption;

    [ObservableProperty] private bool _isTypeEnabled = true;

    [ObservableProperty] private string? _syntax;

    [ObservableProperty] private string? _tags;

    [ObservableProperty] private bool _isReadOnly;

    public NoteNodeEditRow()
    {
        _selectedTypeOption = TypeOptions[0];
    }

    public NoteNodeType SelectedType => SelectedTypeOption?.Value ?? NoteNodeType.Note;
}

public sealed record NodeTypeOption(string Display, NoteNodeType Value);