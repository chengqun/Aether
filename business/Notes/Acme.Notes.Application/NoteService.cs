using Acme.Notes.Domain;

namespace Acme.Notes.Application;

public sealed class NoteService : INoteService
{
    private readonly INoteRepository _repository;

    public NoteService(INoteRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<List<NoteDto>> GetAllAsync(CancellationToken ct = default)
    {
        var nodes = await _repository.GetAllAsync(ct);
        return nodes.Select(ToDto).ToList();
    }

    public async Task<NoteDto?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return null;
        var node = await _repository.GetAsync(guid, ct);
        return node is null ? null : ToDto(node);
    }

    public async Task<List<NoteDto>> GetChildrenAsync(string? parentId, CancellationToken ct = default)
    {
        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(parentId) && Guid.TryParse(parentId, out var parsed))
            guid = parsed;
        var nodes = await _repository.GetChildrenAsync(guid, ct);
        return nodes.Select(ToDto).ToList();
    }

    public async Task<NoteDto> CreateAsync(CreateNoteRequest request, CancellationToken ct = default)
    {
        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(request.ParentId) && Guid.TryParse(request.ParentId, out var parsed))
            parentId = parsed;

        var node = new NoteNode
        {
            ParentId = parentId,
            Title = request.Title,
            Content = request.Content,
            Syntax = request.Syntax,
            Tags = request.Tags,
        };

        var created = await _repository.CreateAsync(node, ct);
        return ToDto(created);
    }

    public async Task UpdateAsync(UpdateNoteRequest request, CancellationToken ct = default)
    {
        if (!Guid.TryParse(request.Id, out var id))
            throw new ArgumentException("无效的笔记 ID", nameof(request.Id));

        var node = await _repository.GetAsync(id, ct);
        if (node is null)
            throw new KeyNotFoundException("找不到指定的笔记");

        node.Title = request.Title;
        node.Content = request.Content;
        if (request.Syntax is not null)
            node.Syntax = request.Syntax;
        if (request.Tags is not null)
            node.Tags = request.Tags;

        await _repository.UpdateAsync(node, ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            throw new ArgumentException("无效的笔记 ID", nameof(id));
        await _repository.DeleteAsync(guid, ct);
    }

    public async Task<List<NoteDto>> SearchAsync(string query, CancellationToken ct = default)
    {
        var nodes = await _repository.SearchAsync(query, ct);
        return nodes.Select(ToDto).ToList();
    }

    private static NoteDto ToDto(NoteNode node) => new()
    {
        Id = node.Id.ToString(),
        ParentId = node.ParentId?.ToString(),
        Title = node.Title,
        Content = node.Content,
        Syntax = node.Syntax,
        Tags = node.Tags,
        IsReadOnly = node.IsReadOnly,
    };
}