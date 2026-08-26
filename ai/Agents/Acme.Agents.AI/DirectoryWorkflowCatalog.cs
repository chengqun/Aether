using Acme.Notes.Domain.Agents;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomWorkflowCatalog"/> 的 labs 目录版实现：读写 <c>&lt;labsRoot&gt;/workflows/*.workflow.json</c> 文件。
/// 与原 note 版 catalog 同契约、互不相干。workflow 的 <c>Id</c> 是 <see cref="Guid"/>
/// （<c>CustomWorkflowDefinition.Id</c>），<c>IWorkflowSourceParser</c> 的 JSON 序列化已含 <c>Id</c> 字段，故 labs 文件天然带 id。
/// 参与者/转交边引用仍是 Guid（目标 agent 文件 front-matter 的 <c>id:</c>），复用 note 版模型零改动。
/// labs 根经 <c>Func&lt;string?&gt;</c> 注入（宿主绑 <c>IKnowledgeDirectoryProvider</c>），本适配器不引 apps 层。
/// </summary>
public sealed class DirectoryWorkflowCatalog : ICustomWorkflowCatalog
{
    private readonly Func<string?> _labsRoot;
    private readonly IWorkflowSourceParser _parser;

    public DirectoryWorkflowCatalog(Func<string?> labsRoot, IWorkflowSourceParser parser)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    private string? Root => _labsRoot();
    private const string Ext = ".workflow.json";

    public async Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return Array.Empty<CustomWorkflowDefinition>();

        var dir = Path.Combine(root, "workflows");
        if (!Directory.Exists(dir)) return Array.Empty<CustomWorkflowDefinition>();

        var result = new List<CustomWorkflowDefinition>();
        foreach (var file in Directory.GetFiles(dir, "*" + Ext, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
            catch { continue; }
            if (_parser.TryParse(content, out var def, out _) && def is not null)
                result.Add(def);
        }
        return result;
    }

    public async Task<CustomWorkflowDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return null;
        var dir = Path.Combine(root, "workflows");
        if (!Directory.Exists(dir)) return null;

        foreach (var file in Directory.GetFiles(dir, "*" + Ext, SearchOption.TopDirectoryOnly))
        {
            string content;
            try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
            catch { continue; }
            if (_parser.TryParse(content, out var def, out _) && def is not null && def.Id == id)
                return def;
        }
        return null;
    }

    public async Task SaveAsync(CustomWorkflowDefinition definition, CancellationToken ct = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("名称不能为空", nameof(definition));

        var root = Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("未打开 labs 目录");

        if (definition.Id == Guid.Empty) definition.Id = Guid.NewGuid();
        var source = _parser.Serialize(definition);

        var dir = Path.Combine(root, "workflows");
        Directory.CreateDirectory(dir);

        var existing = FindFileById(dir, definition.Id);
        if (existing is not null)
        {
            await File.WriteAllTextAsync(existing, source, ct).ConfigureAwait(false);
            return;
        }

        var full = Path.Combine(dir, $"{Sanitize(definition.Name)}{Ext}");
        await File.WriteAllTextAsync(full, source, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return;
        var dir = Path.Combine(root, "workflows");
        if (!Directory.Exists(dir)) return;
        var found = FindFileById(dir, id);
        if (found is not null)
        {
            try { File.Delete(found); } catch { /* 删除失败不阻断 */ }
        }
    }

    private string? FindFileById(string dir, Guid id)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var file in Directory.GetFiles(dir, "*" + Ext, SearchOption.TopDirectoryOnly))
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }
            if (_parser.TryParse(content, out var def, out _) && def is not null && def.Id == id)
                return file;
        }
        return null;
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var trimmed = sb.ToString().Trim();
        return string.IsNullOrEmpty(trimmed) ? "untitled" : trimmed;
    }
}
