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

    /// <summary>可选图标列表（每行独立实例，避免选中态跨弹窗串扰）。</summary>
    public ObservableCollection<NoteIconOption> IconOptions { get; } =
        new(NoteIcons.Catalog.Select(o => new NoteIconOption(o.Display, o.Key, o.PathData, o.Color)));

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private NodeTypeOption? _selectedTypeOption;

    [ObservableProperty] private bool _isTypeEnabled = true;

    [ObservableProperty] private string? _syntax;

    [ObservableProperty] private string? _tags;

    [ObservableProperty] private bool _isReadOnly;

    public NoteNodeEditRow()
    {
        _selectedTypeOption = TypeOptions[0];
        SelectIconByKey(null);
    }

    public NoteNodeType SelectedType => SelectedTypeOption?.Value ?? NoteNodeType.Note;

    /// <summary>当前选中图标的 key（null=跟随类型默认）。</summary>
    public string? IconKey => IconOptions.FirstOrDefault(o => o.IsSelected)?.Key;

    /// <summary>按 key 选中图标项（单选互斥）；null 或未命中时选中「默认」。</summary>
    public void SelectIconByKey(string? key)
    {
        var match = Array.Find(IconOptions.ToArray(), o => o.Key == key) ?? IconOptions[0];
        foreach (var o in IconOptions) o.IsSelected = ReferenceEquals(o, match);
    }
}

public sealed record NodeTypeOption(string Display, NoteNodeType Value);