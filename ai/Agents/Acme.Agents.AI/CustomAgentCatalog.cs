using Acme.Notes.Application;
using Acme.Notes.AgentSource;
using Acme.Notes.Domain;
using Aether.AI.Harness.Tools;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomAgentCatalog"/> 默认实现：用 <see cref="INoteService"/> 持久化、
/// <see cref="AgentSourceParser"/> 序列化，工具元数据取自 <see cref="ToolRegistry"/>。
/// </summary>
public sealed class CustomAgentCatalog : ICustomAgentCatalog
{
    private readonly INoteService _notes;
    private readonly ToolRegistry _toolRegistry;
    private readonly IModelSource _modelSource;

    public CustomAgentCatalog(INoteService notes, ToolRegistry toolRegistry, IModelSource modelSource)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _modelSource = modelSource ?? throw new ArgumentNullException(nameof(modelSource));
    }

    public async Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default)
    {
        var all = await _notes.GetAllAsync(ct);
        var result = new List<CustomAgentDefinition>();
        foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Agent))
        {
            if (AgentSourceParser.TryParse(note.Content, out var fields, out _) && fields is not null)
            {
                result.Add(ToDefinition(note.Id, fields));
            }
        }
        return result;
    }

    public async Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var note = await _notes.GetAsync(id.ToString(), ct);
        if (note is null || note.NodeType != NoteNodeType.Agent) return null;
        if (!AgentSourceParser.TryParse(note.Content, out var fields, out _) || fields is null) return null;
        return ToDefinition(note.Id, fields);
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

        var source = AgentSourceParser.Serialize(definition.ToSourceFields());

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
            .Select(m => new CustomToolMeta(m.Name, m.Description, m.Category))
            .ToList();

    public IReadOnlyList<CustomModelOption> GetAvailableModels()
        => _modelSource.GetModels();

    private static CustomAgentDefinition ToDefinition(string noteId, AgentSourceFields fields)
        => new()
        {
            Id = Guid.TryParse(noteId, out var gid) ? gid : Guid.NewGuid(),
            Name = fields.Name,
            Description = fields.Description,
            Category = string.IsNullOrWhiteSpace(fields.Category) ? "普通对话" : fields.Category,
            SystemPrompt = fields.Prompt,
            ModelId = fields.Model,
            Tools = fields.Tools,
            Memory = fields.Memory,
            MaxHistory = fields.MaxHistory,
            Source = fields.Source,
            MarketplaceId = fields.MarketplaceId,
            MarketplaceSyncedAt = fields.MarketplaceSyncedAt,
            DisplayName = fields.DisplayName,
            TopP = fields.TopP,
            MaxOutputTokens = fields.MaxOutputTokens,
            ResponseFormat = fields.ResponseFormat,
            EnableWebSearch = fields.EnableWebSearch,
            EnableTodoProvider = fields.EnableTodoProvider,
            EnableAgentModeProvider = fields.EnableAgentModeProvider,
            EnableFileMemory = fields.EnableFileMemory,
            EnableAgentSkills = fields.EnableAgentSkills,
            EnableOpenTelemetry = fields.EnableOpenTelemetry,
        };
}
