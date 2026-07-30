using Acme.Notes.Domain;

namespace Acme.Notes.Application;

public sealed class NoteDto
{
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? Syntax { get; init; }
    public string? Tags { get; init; }
    public bool IsReadOnly { get; init; }
    public NoteNodeType NodeType { get; init; } = NoteNodeType.Note;
    public string? Icon { get; init; }
}

public sealed class CreateNoteRequest
{
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string? Syntax { get; init; }
    public string? Tags { get; init; }
    public NoteNodeType NodeType { get; init; } = NoteNodeType.Note;
    public string? Icon { get; init; }
}

public sealed class UpdateNoteRequest
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? Syntax { get; init; }
    public string? Tags { get; init; }
    public string? ParentId { get; init; }
    public NoteNodeType? NodeType { get; init; }
    /// <summary>null=不改；空串=清除自定义图标（回退 NodeType 默认）；非空=设为该 key。</summary>
    public string? Icon { get; init; }
}