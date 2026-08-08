using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>IStrategyNoteSource 的 host 实现：桥接 Notes 上下文（INoteService）→ Trading.Scripting。
/// 让 Trading.Scripting 不直接依赖 Notes.Application，通过 Domain 端口隔离。
/// 此处是唯一知 NoteNodeType 语义的地方——按节点类型过滤策略笔记并标记种类：
/// CodeStrategy → Code（Roslyn），Strategy → Yaml（声明式配方）。</summary>
public sealed class StrategyNoteSource : IStrategyNoteSource
{
    private readonly INoteService _notes;
    public StrategyNoteSource(INoteService notes) => _notes = notes;

    public async Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _notes.GetAllAsync(cancellationToken);
        return all
            .Where(n => n.NodeType is NoteNodeType.CodeStrategy or NoteNodeType.Strategy)
            .Select(n => new StrategyNote(
                n.Id,
                n.Title,
                n.Content ?? "",
                Kind: n.NodeType == NoteNodeType.Strategy ? StrategyKind.Yaml : StrategyKind.Code))
            .ToList();
    }
}
