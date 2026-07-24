namespace Acme.Notes.Domain;

/// <summary>
/// Note node repository (local SQLite). Tree CRUD + search. No userId — single-user device.
/// </summary>
public interface INoteRepository
{
    Task<List<NoteNode>> GetAllAsync(CancellationToken ct = default);
    Task<NoteNode?> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<NoteNode>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default);
    Task<NoteNode> CreateAsync(NoteNode node, CancellationToken ct = default);
    Task UpdateAsync(NoteNode node, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<List<NoteNode>> SearchAsync(string query, CancellationToken ct = default);
}
