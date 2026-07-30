namespace Acme.Notes.Application;

public interface INoteService
{
    /// <summary>笔记被增删改后触发（如 AI 工具修改），订阅方可据此刷新视图。</summary>
    event Action? NotesChanged;

    Task<List<NoteDto>> GetAllAsync(CancellationToken ct = default);
    Task<NoteDto?> GetAsync(string id, CancellationToken ct = default);
    Task<List<NoteDto>> GetChildrenAsync(string? parentId, CancellationToken ct = default);
    Task<NoteDto> CreateAsync(CreateNoteRequest request, CancellationToken ct = default);
    Task UpdateAsync(UpdateNoteRequest request, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<List<NoteDto>> SearchAsync(string query, CancellationToken ct = default);
}