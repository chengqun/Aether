using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Trading.Runtime.Domain;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>
/// Lean 算法笔记源（<see cref="ILeanAlgorithmNoteSource"/> 实现）：按 <see cref="NoteNodeType.LeanAlgorithm"/>
/// 过滤笔记并映射成 <see cref="LeanAlgorithmNote"/>。与 <c>StrategyNoteSource</c> 平行、互不耦合。
/// 此处是唯一知 LeanAlgorithm 节点类型语义的地方；Lean 算法脚本服务本身与 Notes 无关。
/// </summary>
public sealed class LeanAlgorithmNoteSource : ILeanAlgorithmNoteSource
{
    private readonly INoteService _notes;
    public LeanAlgorithmNoteSource(INoteService notes) => _notes = notes;

    public async Task<IReadOnlyList<LeanAlgorithmNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _notes.GetAllAsync(cancellationToken);
        return all
            .Where(n => n.NodeType == NoteNodeType.LeanAlgorithm)
            .Select(n => new LeanAlgorithmNote(n.Id, n.Title, n.Content ?? ""))
            .ToList();
    }
}
