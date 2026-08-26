using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomAgentCatalog"/> 的 labs 目录版实现：读写 <c>&lt;labsRoot&gt;/agents|rules|coderules/*</c> 文件
/// （而非 note 节点）。身份沿用 <see cref="CustomAgentDefinition.Id"/>（Guid），**存于文件 front-matter 的 <c>id:</c> 字段**
/// ——文件重命名不断链（id 在内容不在路径）。labs 目录版 catalog 同契约；
/// 桌面端 DI 默认换注册到本实现（note 版保留但不作为权威）。
/// labs 根经 <c>Func&lt;string?&gt;</c> 注入（宿主绑 <c>IKnowledgeDirectoryProvider</c>），本适配器不引 apps 层。
/// 规则 agent（NodeType==Rule）经 <see cref="IRuleSourceParser"/>（单 YAML + id 字段）、代码规则（CodeRule）
/// 经 <see cref="ICodeSourceParser"/>、普通 agent 经 <see cref="IAgentSourceParser"/>。规则程序经
/// <see cref="RuleProgramYaml"/> 注册进 <see cref="RuleRegistry"/>（与本适配器 note 版同源）。
/// </summary>
public sealed class DirectoryAgentCatalog : ICustomAgentCatalog
{
    private readonly Func<string?> _labsRoot;
    private readonly ToolRegistry _toolRegistry;
    private readonly IModelSource _modelSource;
    private readonly IAgentSourceParser _agentParser;
    private readonly IRuleSourceParser _ruleParser;
    private readonly ICodeSourceParser _codeParser;
    private readonly RuleRegistry _ruleRegistry;

    public DirectoryAgentCatalog(
        Func<string?> labsRoot,
        ToolRegistry toolRegistry,
        IModelSource modelSource,
        IAgentSourceParser agentParser,
        IRuleSourceParser ruleParser,
        ICodeSourceParser codeParser,
        RuleRegistry ruleRegistry)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
        _agentParser = agentParser ?? throw new ArgumentNullException(nameof(agentParser));
        _ruleParser = ruleParser ?? throw new ArgumentNullException(nameof(ruleParser));
        _codeParser = codeParser ?? throw new ArgumentNullException(nameof(codeParser));
        _ruleRegistry = ruleRegistry ?? throw new ArgumentNullException(nameof(ruleRegistry));
    }

    private static readonly (NoteNodeType Type, string Dir, string Ext)[] AgentLayouts =
    {
        (NoteNodeType.Agent, "agents", ".agent.md"),
        (NoteNodeType.Rule, "rules", ".rule.yaml"),
        (NoteNodeType.CodeRule, "coderules", ".coderule.cs"),
    };

    /// <summary>按节点类型选解析器（镜像 note 版 CustomAgentCatalog.TryParseNote）。</summary>
    private bool TryParseNote(NoteNodeType nodeType, string content, out CustomAgentDefinition? def, out string error)
        => nodeType == NoteNodeType.Rule ? _ruleParser.TryParse(content, out def, out error)
         : nodeType == NoteNodeType.CodeRule ? _codeParser.TryParse(content, out def, out error)
                                             : _agentParser.TryParse(content, out def, out error);

    private string? Root => _labsRoot();

    public async Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return Array.Empty<CustomAgentDefinition>();

        var result = new List<CustomAgentDefinition>();
        foreach (var (type, dir, ext) in AgentLayouts)
        {
            ct.ThrowIfCancellationRequested();
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir)) continue;
            foreach (var file in Directory.GetFiles(fullDir, "*" + ext, SearchOption.TopDirectoryOnly))
            {
                string content;
                try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
                catch { continue; }

                if (TryParseNote(type, content, out var def, out _) && def is not null)
                {
                    def.NodeType = type;
                    // 规则 agent 的程序注册进 RuleRegistry（幂等覆盖；目录版与 note 版同行为）。
                    if (type == NoteNodeType.Rule
                        && def.Engine.Equals("Rule", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(def.SystemPrompt)
                        && RuleProgramYaml.TryDeserialize(def.SystemPrompt, out var prog) && prog is not null)
                    {
                        _ruleRegistry.Register(def.Name, prog);
                    }
                    result.Add(def);
                }
            }
        }
        return result;
    }

    public async Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return null;

        foreach (var (type, dir, ext) in AgentLayouts)
        {
            ct.ThrowIfCancellationRequested();
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir)) continue;
            foreach (var file in Directory.GetFiles(fullDir, "*" + ext, SearchOption.TopDirectoryOnly))
            {
                string content;
                try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
                catch { continue; }
                if (TryParseNote(type, content, out var def, out _) && def is not null && def.Id == id)
                {
                    def.NodeType = type;
                    return def;
                }
            }
        }
        return null;
    }

    public async Task<CustomAgentDefinition?> GetByMarketplaceIdAsync(Guid marketplaceId, CancellationToken ct = default)
    {
        var all = await ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(d => d.MarketplaceId == marketplaceId);
    }

    public async Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("名称不能为空", nameof(definition));

        var root = Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("未打开 labs 目录");

        if (definition.Id == Guid.Empty) definition.Id = Guid.NewGuid();
        var (_, dir, ext) = AgentLayouts.FirstOrDefault(l => l.Type == definition.NodeType);
        if (string.IsNullOrEmpty(dir)) dir = "agents";
        if (string.IsNullOrEmpty(ext)) ext = ".agent.md";

        var source = definition.NodeType == NoteNodeType.Rule
            ? _ruleParser.Serialize(definition)
            : definition.NodeType == NoteNodeType.CodeRule
                ? _codeParser.Serialize(definition)
                : _agentParser.Serialize(definition);

        var fullDir = Path.Combine(root, dir);
        Directory.CreateDirectory(fullDir);

        // 已存在（按 id 找）→ 原地重写
        var existing = FindFileByTypeAndId(fullDir, ext, definition.NodeType, definition.Id);
        if (existing is not null)
        {
            await File.WriteAllTextAsync(existing, source, ct).ConfigureAwait(false);
            return;
        }

        var full = Path.Combine(fullDir, $"{Sanitize(definition.Name)}{ext}");
        await File.WriteAllTextAsync(full, source, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return;

        foreach (var (type, dir, ext) in AgentLayouts)
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir)) continue;
            var found = FindFileByTypeAndId(fullDir, ext, type, id);
            if (found is not null)
            {
                try { File.Delete(found); } catch { /* 删除失败不阻断 */ }
                return;
            }
        }
    }

    public IReadOnlyList<CustomToolMeta> GetAvailableTools()
        => _toolRegistry.GetAllMetas()
            .Where(m => !string.Equals(m.Name, "ask_user", StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.Category, "MCP", StringComparison.OrdinalIgnoreCase))
            .Select(m => new CustomToolMeta(m.Name, m.Description, m.Category, m.ApprovalLevel.ToString()))
            .ToList();

    public IReadOnlyList<CustomModelOption> GetAvailableModels()
        => _modelSource.GetModels();

    /// <summary>按 id 在指定类型目录里找文件（扫描文件 front-matter 的 id:）。</summary>
    private string? FindFileByTypeAndId(string fullDir, string ext, NoteNodeType type, Guid id)
    {
        if (!Directory.Exists(fullDir)) return null;
        foreach (var file in Directory.GetFiles(fullDir, "*" + ext, SearchOption.TopDirectoryOnly))
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }
            if (TryParseNote(type, content, out var def, out _) && def is not null && def.Id == id)
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
