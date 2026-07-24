namespace Acme.Notes.Application;

public interface INoteService
{
    Task<List<NoteDto>> GetAllAsync(CancellationToken ct = default);
    Task<NoteDto?> GetAsync(string id, CancellationToken ct = default);
    Task<List<NoteDto>> GetChildrenAsync(string? parentId, CancellationToken ct = default);
    Task<NoteDto> CreateAsync(CreateNoteRequest request, CancellationToken ct = default);
    Task UpdateAsync(UpdateNoteRequest request, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<List<NoteDto>> SearchAsync(string query, CancellationToken ct = default);
}