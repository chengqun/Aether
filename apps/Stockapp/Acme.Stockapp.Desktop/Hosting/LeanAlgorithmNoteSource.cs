using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Acme.Trading.Runtime.Domain;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>
/// Lean 算法笔记源（<see cref="ILeanAlgorithmNoteSource"/> 实现）：按 <see cref="NoteNodeType.LeanAlgorithm"/>
/// 过滤笔记并映射成 <see cref="LeanAlgorithmNote"/>。与 <c>StrategyNoteSource</c> 平行、互不耦合。
/// 此处是唯一知 LeanAlgorithm 节点类型语义的地方；Lean 算法脚本服务本身与 Notes 无关。
/// 经 <see cref="INoteCatalogSource"/> 读，可切目录源。
/// </summary>
public sealed class LeanAlgorithmNoteSource : ILeanAlgorithmNoteSource
{
    private readonly INoteCatalogSource _source;
    public LeanAlgorithmNoteSource(INoteCatalogSource source) => _source = source;

    public async Task<IReadOnlyList<LeanAlgorithmNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _source.EnumerateAsync(NoteNodeType.LeanAlgorithm, cancellationToken);
        return all
            .Select(n => new LeanAlgorithmNote(n.ExternalId, n.Name, n.Content))
            .ToList();
    }
}
