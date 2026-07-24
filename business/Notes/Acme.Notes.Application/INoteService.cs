using Acme.Notes.Domain;

namespace Acme.Notes.Application;

/// <summary>
/// Note application service — thin use-case layer over <see cref="INoteRepository"/>.
/// AI tools inject this, not the repository directly (AI → Application → Domain).
/// </summary>
public interface INoteService
{
    Task<List<NoteNode>> GetAllAsync(CancellationToken ct = default);
    Task<NoteNode?> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<NoteNode>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default);
    Task<NoteNode> CreateAsync(NoteNode node, CancellationToken ct = default);
    Task UpdateAsync(NoteNode node, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<List<NoteNode>> SearchAsync(string query, CancellationToken ct = default);
}
