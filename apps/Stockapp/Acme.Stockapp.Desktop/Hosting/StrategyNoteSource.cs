using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Trading.Runtime.Domain;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>IStrategyNoteSource 的 host 实现：桥接 Notes 上下文（经 <see cref="INoteCatalogSource"/>）→ Trading.Scripting。
/// 让 Trading.Scripting 不直接依赖 Notes.Application，通过 Domain 端口隔离。
/// 此处是唯一知 NoteNodeType 语义的地方——按节点类型过滤策略笔记并标记种类：
/// CodeStrategy → Code（Roslyn），Strategy → Yaml（声明式配方）。经 INoteCatalogSource 读，可切目录源。</summary>
public sealed class StrategyNoteSource : IStrategyNoteSource
{
    private readonly INoteCatalogSource _source;
    public StrategyNoteSource(INoteCatalogSource source) => _source = source;

    public async Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var code = await _source.EnumerateAsync(NoteNodeType.CodeStrategy, cancellationToken);
        var yaml = await _source.EnumerateAsync(NoteNodeType.Strategy, cancellationToken);
        return code
            .Select(n => new StrategyNote(n.ExternalId, n.Name, n.Content, StrategyKind.Code))
            .Concat(yaml.Select(n => new StrategyNote(n.ExternalId, n.Name, n.Content, StrategyKind.Yaml)))
            .ToList();
    }
}
