namespace Acme.Notes.Domain;

/// <summary>
/// A note node (local Markdown note in SQLite). Tree-structured (ParentId + SortOrder).
/// No UserId — the local device is single-user. Ported from AlphaAgent's NoteNode.
/// </summary>
public sealed class NoteNode
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Syntax { get; set; }
    public string? Tags { get; set; }
    public bool IsReadOnly { get; set; }
    public NoteNodeType NodeType { get; set; } = NoteNodeType.Note;
    public Guid? LinkedId { get; set; }
    public string? Source { get; set; }
    public Guid? MarketplaceId { get; set; }
    public DateTimeOffset? MarketplaceSyncedAt { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
