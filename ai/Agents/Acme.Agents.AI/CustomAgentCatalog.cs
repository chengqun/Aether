using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomAgentCatalog"/> 默认实现：用 <see cref="INoteService"/> 持久化、
/// <see cref="IAgentSourceParser"/> 序列化，工具元数据取自 <see cref="ToolRegistry"/>。
/// </summary>
public sealed class CustomAgentCatalog : ICustomAgentCatalog
{
    private readonly INoteService _notes;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly IModelSource _modelSource;
    private readonly IAgentSourceParser _agentParser;

    public CustomAgentCatalog(INoteService notes, ToolRegistry toolRegistry, SkillRegistry skillRegistry, IModelSource modelSource, IAgentSourceParser agentParser)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
        _agentParser = agentParser ?? throw new ArgumentNullException(nameof(agentParser));
    }

    public async Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
    {
        var all = await _notes.GetAllAsync(ct);
        var result = new List<CustomAgentDefinition>();
        foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Agent))
        {
            if (_agentParser.TryParse(note.Content, out var def, out _) && def is not null)
            {
                def.Id = Guid.TryParse(note.Id, out var gid) ? gid : Guid.NewGuid();
                result.Add(def);
            }
        }
        return result;
    }

    public async Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var note = await _notes.GetAsync(id.ToString(), ct);
        if (note is null || note.NodeType != NoteNodeType.Agent) return null;
        if (!_agentParser.TryParse(note.Content, out var def, out _) || def is null) return null;
        def.Id = id;
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

        var source = _agentParser.Serialize(definition);

        var existing = await _notes.GetAsync(definition.Id.ToString(), ct);
        if (existing is null)
        {
            await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = definition.Name,
                Content = source,
                NodeType = NoteNodeType.Agent,
            }, ct);
        }
        else
        {
            await _notes.UpdateAsync(new UpdateNoteRequest
            {
                Id = definition.Id.ToString(),
                Title = definition.Name,
                Content = source,
                NodeType = NoteNodeType.Agent,
            }, ct);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _notes.DeleteAsync(id.ToString(), ct);

    public IReadOnlyList<CustomToolMeta> GetAvailableTools()
        => _toolRegistry.GetAllMetas()
            .Where(m => !string.Equals(m.Name, "ask_user", StringComparison.OrdinalIgnoreCase)) // ask_user 为自动纳入的基础设施工具，不在选择器暴露
            .Select(m => new CustomToolMeta(m.Name, m.Description, m.Category, m.ApprovalLevel.ToString()))
            .ToList();

    public IReadOnlyList<CustomSkillMeta> GetAvailableSkills()
        => _skillRegistry.GetAllMetas()
            .Select(m => new CustomSkillMeta(m.Name, m.Description, m.Category))
            .ToList();

    public IReadOnlyList<CustomModelOption> GetAvailableModels()
        => _modelSource.GetModels();
}
