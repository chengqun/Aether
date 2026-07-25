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
}

public sealed class CreateNoteRequest
{
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string? Syntax { get; init; }
    public string? Tags { get; init; }
    public NoteNodeType NodeType { get; init; } = NoteNodeType.Note;
}

public sealed class UpdateNoteRequest
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string? Syntax { get; init; }
    public string? Tags { get; init; }
    public NoteNodeType? NodeType { get; init; }
}