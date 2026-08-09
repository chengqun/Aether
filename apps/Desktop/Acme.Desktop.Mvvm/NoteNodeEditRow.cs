using System.Collections.ObjectModel;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

public partial class NoteNodeEditRow : ObservableObject
{
    public ObservableCollection<NodeTypeOption> TypeOptions { get; } =
        new(NoteNodeTypeRegistry.All.Select(d => new NodeTypeOption(d.Display, d.Type)));

    /// <summary>可选图标列表（每行独立实例，避免选中态跨弹窗串扰）。</summary>
    public ObservableCollection<NoteIconOption> IconOptions { get; } =
        new(NoteIcons.Catalog.Select(o => new NoteIconOption(o.Display, o.Key, o.PathData, o.Color)));

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private NodeTypeOption? _selectedTypeOption;

    partial void OnSelectedTypeOptionChanged(NodeTypeOption? value)
    {
        // 选有强制语法的类型（Agent/Workflow/Knowledge）时联动语法，
        // 并把图标设为该类型的目录图标（Bot/Workflow/学士帽），而非语法对应的语言 logo。
        if (value is null) return;
        var descriptor = NoteNodeTypeRegistry.Get(value.Value);
        if (descriptor.Syntax is not null)
        {
            SelectedSyntaxOption = SyntaxOptions.FirstOrDefault(o => o.Value == descriptor.Syntax);
            SelectIconByKey(descriptor.IconKey);
        }
    }

    [ObservableProperty] private bool _isTypeEnabled = true;

    /// <summary>语法下拉选项（PathData=图标 path；IsFill=true 用填充渲染语言 logo，false 用描边渲染 Lucide）。</summary>
    public ObservableCollection<SyntaxOption> SyntaxOptions { get; } = new()
    {
        new("Markdown",   "markdown",   NavIcons.LangMarkdown,    true,  "#6B6B70"),
        new("纯文本",     "text",       NavIcons.File,            false, "#6B6B70"),
        new("文件夹",     "folder",     NavIcons.Folder,          false, "#6B6B70"),
        new("SQL",        "sql",        NavIcons.LangSqlite,      true,  "#003B57"),
        new("Python",     "python",     NavIcons.LangPython,      true,  "#3776AB"),
        new("JavaScript", "javascript", NavIcons.LangJavascript,  true,  "#D97706"),
        new("JSON",       "json",       NavIcons.LangJson,        true,  "#6B6B70"),
        new("C#",         "csharp",     NavIcons.LangDotnet,      true,  "#512BD4"),
        new("HTML",       "html",       NavIcons.LangHtml5,       true,  "#E34F26"),
        new("CSS",        "css",        NavIcons.LangCss3,        true,  "#1572B6"),
        new("XML",        "xml",        NavIcons.LangXml,         true,  "#005FAD"),
        new("Java",       "java",       NavIcons.LangOpenJdk,     true,  "#1A1A1F"),
        new("C++",        "cpp",        NavIcons.LangCplusplus,   true,  "#00599C"),
        new("VB",         "vb",         NavIcons.LangDotnet,      true,  "#512BD4"),
        new("PowerShell", "powershell", NavIcons.LangBash,        true,  "#4EAA25"),
        new("YAML",       "yaml",       NavIcons.LangYaml,        true,  "#CB171E"),
    };

    [ObservableProperty] private SyntaxOption? _selectedSyntaxOption;

    partial void OnSelectedSyntaxOptionChanged(SyntaxOption? value)
    {
        // 选语法时自动把节点图标设为对应语言图标（用户仍可在图标区手动改）
        if (value is not null)
            SelectIconByKey(NoteIcons.IconKeyForSyntax(value.Value));
    }

    [ObservableProperty] private string? _tags;

    [ObservableProperty] private bool _isReadOnly;

    public NoteNodeEditRow()
    {
        _selectedTypeOption = TypeOptions[0];
        _selectedSyntaxOption = SyntaxOptions[0]; // 默认 markdown（触发上面的图标联动）
        SelectIconByKey(NoteIcons.IconKeyForSyntax(SyntaxOptions[0].Value));
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

public sealed record SyntaxOption(string Display, string Value, string PathData, bool IsFill, string Color);