using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>笔记树节点 ViewModel（可观测，支持子节点展开）。</summary>
public sealed partial class NoteNodeVM : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private Guid? _parentId;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string? _syntax;
    [ObservableProperty] private string? _tags;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private NoteNodeVM? _parent;

    public ObservableCollection<NoteNodeVM> Children { get; } = new();

    public string Icon => NodeType switch
    {
        NoteNodeType.Note => "📝",
        NoteNodeType.Agent => "🤖",
        NoteNodeType.Workflow => "🤝",
        _ => "📄"
    };

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
        };
        return node;
    }
}
