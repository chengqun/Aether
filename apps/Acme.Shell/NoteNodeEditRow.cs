using System.Collections.ObjectModel;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

public partial class NoteNodeEditRow : ObservableObject
{
    public ObservableCollection<NodeTypeOption> TypeOptions { get; } = new()
    {
        new("文本", NoteNodeType.Note),
        new("Agent", NoteNodeType.Agent),
        new("Workflow", NoteNodeType.Workflow),
    };

    /// <summary>可选图标列表（每行独立实例，避免选中态跨弹窗串扰）。</summary>
    public ObservableCollection<NoteIconOption> IconOptions { get; } =
        new(NoteIcons.Catalog.Select(o => new NoteIconOption(o.Display, o.Key, o.PathData, o.Color)));

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private NodeTypeOption? _selectedTypeOption;

    [ObservableProperty] private bool _isTypeEnabled = true;

    /// <summary>语法下拉选项（值=持久化 key；代码后台按 key 映射 AvaloniaEdit 高亮定义）。</summary>
    public ObservableCollection<SyntaxOption> SyntaxOptions { get; } = new()
    {
        new("Markdown", "markdown"),
        new("纯文本", "text"),
        new("SQL", "sql"),
        new("Python", "python"),
        new("JavaScript", "javascript"),
        new("JSON", "json"),
        new("C#", "csharp"),
        new("HTML", "html"),
        new("CSS", "css"),
        new("XML", "xml"),
        new("Java", "java"),
        new("C++", "cpp"),
        new("VB", "vb"),
        new("PowerShell", "powershell"),
    };

    [ObservableProperty] private SyntaxOption? _selectedSyntaxOption;

    [ObservableProperty] private string? _tags;

    [ObservableProperty] private bool _isReadOnly;

    public NoteNodeEditRow()
    {
        _selectedTypeOption = TypeOptions[0];
        _selectedSyntaxOption = SyntaxOptions[0]; // 默认 markdown
        SelectIconByKey(null);
    }

    public NoteNodeType SelectedType => SelectedTypeOption?.Value ?? NoteNodeType.Note;

    /// <summary>当前语法值（markdown/sql/text）。</summary>
    public string? SyntaxValue => SelectedSyntaxOption?.Value;

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

public sealed record SyntaxOption(string Display, string Value);