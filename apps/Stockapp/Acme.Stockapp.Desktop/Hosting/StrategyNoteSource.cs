using Acme.Notes.Application;
using Acme.Trading.Runtime.Domain;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>IStrategyNoteSource 的 host 实现：桥接 Notes 上下文（INoteService）→ Trading.Scripting。
/// 让 Trading.Scripting 不直接依赖 Notes.Application，通过 Domain 端口隔离。</summary>
public sealed class StrategyNoteSource : IStrategyNoteSource
{
    private readonly INoteService _notes;
    public StrategyNoteSource(INoteService notes) => _notes = notes;

    public async Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _notes.GetAllAsync(cancellationToken);
        return all.Select(n => new StrategyNote(n.Id, n.Title, n.Content ?? "", n.Tags)).ToList();
    }
}
