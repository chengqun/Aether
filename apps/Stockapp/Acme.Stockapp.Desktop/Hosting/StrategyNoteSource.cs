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

    public async Task<string> SaveAsync(string name, string content, StrategyKind kind, CancellationToken cancellationToken = default)
    {
        var root = _source.Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("未打开资料库目录");
        var type = kind == StrategyKind.Code ? NoteNodeType.CodeStrategy : NoteNodeType.Strategy;
        var layout = CatalogNoteDirectory.For(type);
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
