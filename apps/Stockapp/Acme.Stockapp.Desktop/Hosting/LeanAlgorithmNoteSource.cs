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

    public async Task<string> SaveAsync(string name, string content, CancellationToken cancellationToken = default)
    {
        var root = _source.Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("未打开资料库目录");
        var layout = CatalogNoteDirectory.For(NoteNodeType.LeanAlgorithm);
        var fullDir = Path.Combine(root, layout.DirectoryName);
        Directory.CreateDirectory(fullDir);
        var safe = Sanitize(name);
        var path = Path.Combine(fullDir, safe + layout.Extension);
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return Path.GetRelativePath(root, path);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(invalid.Contains(c) ? '_' : c);
        var t = sb.ToString();
        return string.IsNullOrEmpty(t) ? "untitled" : t;
    }
}
