using Acme.Notes.Domain;

namespace Acme.Notes.Application;

/// <summary>
/// Thin pass-through to <see cref="INoteRepository"/>. Business logic (validation,
/// events) can be added here later.
/// </summary>
public sealed class NoteService : INoteService
{
    private readonly INoteRepository _repository;

    public NoteService(INoteRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public Task<List<NoteNode>> GetAllAsync(CancellationToken ct = default) => _repository.GetAllAsync(ct);
    public Task<NoteNode?> GetAsync(Guid id, CancellationToken ct = default) => _repository.GetAsync(id, ct);
    public Task<List<NoteNode>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default) => _repository.GetChildrenAsync(parentId, ct);
    public Task<NoteNode> CreateAsync(NoteNode node, CancellationToken ct = default) => _repository.CreateAsync(node, ct);
    public Task UpdateAsync(NoteNode node, CancellationToken ct = default) => _repository.UpdateAsync(node, ct);
    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _repository.DeleteAsync(id, ct);
    public Task<List<NoteNode>> SearchAsync(string query, CancellationToken ct = default) => _repository.SearchAsync(query, ct);
}
