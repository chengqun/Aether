using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Skills;
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
    private readonly INoteService _notes;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly IModelSource _modelSource;
    private readonly IAgentSourceParser _agentParser;
    private readonly IRuleSourceParser _ruleParser;
    private readonly RuleRegistry _ruleRegistry;

    public CustomAgentCatalog(INoteService notes, ToolRegistry toolRegistry, SkillRegistry skillRegistry, IModelSource modelSource, IAgentSourceParser agentParser, IRuleSourceParser ruleParser, RuleRegistry ruleRegistry)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
        _agentParser = agentParser ?? throw new ArgumentNullException(nameof(agentParser));
        _ruleParser = ruleParser ?? throw new ArgumentNullException(nameof(ruleParser));
        _ruleRegistry = ruleRegistry ?? throw new ArgumentNullException(nameof(ruleRegistry));
    }

    /// <summary>按节点类型选解析器：Rule→规则单 YAML 解析器；其余→agent front-matter 解析器。</summary>
    private bool TryParseNote(NoteNodeType nodeType, string content, out CustomAgentDefinition? def, out string error)
        => nodeType == NoteNodeType.Rule ? _ruleParser.TryParse(content, out def, out error)
                                         : _agentParser.TryParse(content, out def, out error);

    public async Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
    {
        var all = await _notes.GetAllAsync(ct);
        var result = new List<CustomAgentDefinition>();
        foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Agent || n.NodeType == NoteNodeType.Rule))
        {
            if (TryParseNote(note.NodeType, note.Content, out var def, out _) && def is not null)
            {
                def.Id = Guid.TryParse(note.Id, out var gid) ? gid : Guid.NewGuid();
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
        var note = await _notes.GetAsync(id.ToString(), ct);
        if (note is null || (note.NodeType != NoteNodeType.Agent && note.NodeType != NoteNodeType.Rule)) return null;
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

    public IReadOnlyList<CustomSkillMeta> GetAvailableSkills()
        => _skillRegistry.GetAllMetas()
            .Select(m => new CustomSkillMeta(m.Name, m.Description, m.Category))
            .ToList();

    public IReadOnlyList<CustomModelOption> GetAvailableModels()
        => _modelSource.GetModels();
}
