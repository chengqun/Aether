using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Domain.Catalog;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomAgentCatalog"/> 默认实现：用 <see cref="INoteService"/> 持久化、
/// 按 <see cref="NoteNodeType"/> 分发解析器（Agent→<see cref="IAgentSourceParser"/> front-matter+body；
/// Rule→<see cref="IRuleSourceParser"/> 单 YAML 文档），工具元数据取自 <see cref="ToolRegistry"/>。
/// 同时承载普通 agent（<see cref="NoteNodeType.Agent"/>）与规则 agent（<see cref="NoteNodeType.Rule"/>）。
/// </summary>
public sealed class CustomAgentCatalog : ICustomAgentCatalog
{
    private readonly INoteService _notes;        // 写路径（Save/Delete/Create）仍走 SQLite
    private readonly INoteCatalogSource _source; // 读路径（List/Get）走抽象，可切换目录源
    private readonly ToolRegistry _toolRegistry;
    private readonly IModelSource _modelSource;
    private readonly IAgentSourceParser _agentParser;
    private readonly IRuleSourceParser _ruleParser;
    private readonly ICodeSourceParser _codeParser;
    private readonly RuleRegistry _ruleRegistry;

    public CustomAgentCatalog(INoteService notes, INoteCatalogSource source, ToolRegistry toolRegistry, IModelSource modelSource, IAgentSourceParser agentParser, IRuleSourceParser ruleParser, ICodeSourceParser codeParser, RuleRegistry ruleRegistry)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
        _agentParser = agentParser ?? throw new ArgumentNullException(nameof(agentParser));
        _ruleParser = ruleParser ?? throw new ArgumentNullException(nameof(ruleParser));
        _codeParser = codeParser ?? throw new ArgumentNullException(nameof(codeParser));
        _ruleRegistry = ruleRegistry ?? throw new ArgumentNullException(nameof(ruleRegistry));
    }

    /// <summary>按节点类型选解析器：Rule→规则单 YAML；CodeRule→代码规则 front-matter+C#；其余→agent front-matter+body。</summary>
    private bool TryParseNote(NoteNodeType nodeType, string content, out CustomAgentDefinition? def, out string error)
        => nodeType == NoteNodeType.Rule ? _ruleParser.TryParse(content, out def, out error)
         : nodeType == NoteNodeType.CodeRule ? _codeParser.TryParse(content, out def, out error)
                                             : _agentParser.TryParse(content, out def, out error);

    public async Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
    {
        // 读三种 agent 家族类型；目录源下 Agent/Rule/CodeRule 从 labs/agents|rules|coderules 枚举。
        var byType = new List<CatalogNote>();
        foreach (var type in new[] { NoteNodeType.Agent, NoteNodeType.Rule, NoteNodeType.CodeRule })
            byType.AddRange(await _source.EnumerateAsync(type, ct));

        var result = new List<CustomAgentDefinition>();
        foreach (var note in byType)
        {
            if (TryParseNote(note.NodeType, note.Content, out var def, out _) && def is not null)
            {
                def.Id = Guid.TryParse(note.ExternalId, out var gid) ? gid : Guid.NewGuid();
                def.NodeType = note.NodeType;
                // 规则 agent 的程序同时注册进 RuleRegistry，供 LLM agent 经 run_rule(name) 调用。
                // 幂等覆盖写（Register），ListAsync 每次重注册安全；解析失败静默跳过（不阻塞列表）。
                if (note.NodeType == NoteNodeType.Rule
                    && def.Engine.Equals("Rule", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(def.SystemPrompt)
                    && RuleProgramYaml.TryDeserialize(def.SystemPrompt, out var prog) && prog is not null)
                {
                    _ruleRegistry.Register(def.Name, prog);
                }
                result.Add(def);
            }
        }
        return result;
    }

    public async Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var note = await _source.FindByExternalIdAsync(id.ToString(), ct);
        if (note is null || (note.NodeType != NoteNodeType.Agent && note.NodeType != NoteNodeType.Rule && note.NodeType != NoteNodeType.CodeRule)) return null;
        if (!TryParseNote(note.NodeType, note.Content, out var def, out _) || def is null) return null;
        def.Id = id;
        def.NodeType = note.NodeType;
        return def;
    }

    public async Task<CustomAgentDefinition?> GetByMarketplaceIdAsync(Guid marketplaceId, CancellationToken ct = default)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(d => d.MarketplaceId == marketplaceId);
    }

    public async Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("名称不能为空", nameof(definition));

        var source = definition.NodeType == NoteNodeType.Rule
            ? _ruleParser.Serialize(definition)
            : definition.NodeType == NoteNodeType.CodeRule
                ? _codeParser.Serialize(definition)
                : _agentParser.Serialize(definition);

        var existing = await _notes.GetAsync(definition.Id.ToString(), ct);
        if (existing is null)
        {
            await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = definition.Name,
                Content = source,
                NodeType = definition.NodeType,
            }, ct);
        }
        else
        {
            await _notes.UpdateAsync(new UpdateNoteRequest
            {
                Id = definition.Id.ToString(),
                Title = definition.Name,
                Content = source,
                NodeType = definition.NodeType,
            }, ct);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _notes.DeleteAsync(id.ToString(), ct);

    public IReadOnlyList<CustomToolMeta> GetAvailableTools()
        => _toolRegistry.GetAllMetas()
            .Where(m => !string.Equals(m.Name, "ask_user", StringComparison.OrdinalIgnoreCase)) // ask_user 为自动纳入的基础设施工具，不在选择器暴露
            .Where(m => !string.Equals(m.Category, "MCP", StringComparison.OrdinalIgnoreCase)) // MCP 工具在编辑器单独的 MCP 区展示
            .Select(m => new CustomToolMeta(m.Name, m.Description, m.Category, m.ApprovalLevel.ToString()))
            .ToList();

    public IReadOnlyList<CustomModelOption> GetAvailableModels()
        => _modelSource.GetModels();
}
