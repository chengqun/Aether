using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

public sealed partial class NoteNodeVM : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private Guid? _parentId;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string? _syntax;

    /// <summary>文本节点且语法为 markdown → 预览时用 Markdown 渲染。</summary>
    public bool IsMarkdown => NodeType == NoteNodeType.Note
        && string.Equals(Syntax, "markdown", StringComparison.OrdinalIgnoreCase);

    partial void OnSyntaxChanged(string? value) => OnPropertyChanged(nameof(IsMarkdown));

    [ObservableProperty] private string? _tags;
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>任意节点展开/折叠变化时触发（静态，供 VM 节流保存展开状态）。</summary>
    public static event Action? AnyExpandChanged;

    partial void OnIsExpandedChanged(bool value) => AnyExpandChanged?.Invoke();
    [ObservableProperty] private NoteNodeVM? _parent;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private int _sortOrder;

    /// <summary>持久化的图标 key（null=按 NodeType 回退默认）。</summary>
    [ObservableProperty] private string? _iconKey;

    partial void OnIconKeyChanged(string? value)
    {
        // Icon / IconColor / IconIsFill 由 IconKey 派生，key 变化时需手动通知绑定刷新。
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IconColor));
        OnPropertyChanged(nameof(IconIsFill));
    }

    public ObservableCollection<NoteNodeVM> Children { get; } = new();

    /// <summary>渲染用矢量 path data：自定义图标优先，否则按 NodeType 注册表默认。</summary>
    public string Icon => NoteIcons.Find(IconKey)?.PathData
        ?? NoteNodeTypeRegistry.Get(NodeType).IconPathData;

    /// <summary>图标配色：自定义图标用其目录色，否则按 NodeType 注册表默认。</summary>
    public string IconColor => NoteIcons.Find(IconKey)?.Color
        ?? NoteNodeTypeRegistry.Get(NodeType).IconColor;

    /// <summary>图标渲染模式：自定义语言 logo=true(填充)，其余 false(描边)。</summary>
    public bool IconIsFill => NoteIcons.Find(IconKey)?.IsFill ?? false;

    public NoteNodeType NodeType { get; set; } = NoteNodeType.Note;

    public static NoteNodeVM FromDto(NoteDto dto)
    {
        var node = new NoteNodeVM
        {
            Id = Guid.TryParse(dto.Id, out var gid) ? gid : Guid.NewGuid(),
            ParentId = Guid.TryParse(dto.ParentId, out var pid) ? pid : null,
            Title = dto.Title,
            Content = dto.Content,
            Syntax = dto.Syntax,
            Tags = dto.Tags,
            NodeType = dto.NodeType,
            IconKey = dto.Icon,
            SortOrder = dto.SortOrder,
        };
        return node;
    }
}
