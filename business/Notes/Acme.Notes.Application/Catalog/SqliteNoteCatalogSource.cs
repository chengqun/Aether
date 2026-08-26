using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;

namespace Acme.Notes.Application.Catalog;

/// <summary>
/// <see cref="INoteCatalogSource"/> 的 SQLite 实现：包 <see cref="INoteService"/>，按
/// <see cref="NoteNodeType"/> 过滤节点映射成 <see cref="CatalogNote"/>。这是默认源——接入现有一切
/// catalog 后**行为与改前完全一致**（只是把「查 <see cref="INoteService"/> 并按 NodeType 过滤」这一步
/// 收口到这一个端口）。<c>Changed</c> 转发 <c>INoteService.NotesChanged</c>。
/// 住宿 Application 层：它组合 <see cref="INoteService"/>（本层契约），镜像 <c>NoteService</c> 包
/// <see cref="INoteRepository"/>（Domain 端口）的层化关系——Application 组合 Domain 端口对外提供更粗粒服务。
/// </summary>
public sealed class SqliteNoteCatalogSource : INoteCatalogSource
{
    private readonly INoteService _notes;

    public SqliteNoteCatalogSource(INoteService notes)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
    }

    /// <summary>SQLite 源无文件根（写入端不可用，仅为满足接口）。</summary>
    public string? Root => null;

    public async Task<IReadOnlyList<CatalogNote>> EnumerateAsync(NoteNodeType? nodeType = null, CancellationToken ct = default)
    {
        var all = await _notes.GetAllAsync(ct);
        return all
            .Where(n => nodeType is null || n.NodeType == nodeType)
            .Select(n => new CatalogNote(n.Id, n.Title, n.Content ?? "", n.NodeType))
            .ToList();
    }

    public async Task<CatalogNote?> FindByExternalIdAsync(string externalId, CancellationToken ct = default)
    {
        var note = await _notes.GetAsync(externalId, ct);
        if (note is null) return null;
        return new CatalogNote(note.Id, note.Title, note.Content ?? "", note.NodeType);
    }

    public event Action? Changed
    {
        add => _notes.NotesChanged += value;
        remove => _notes.NotesChanged -= value;
    }
}
