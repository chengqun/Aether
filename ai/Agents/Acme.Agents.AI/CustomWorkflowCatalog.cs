using Acme.Notes.Application;
using Acme.Notes.AgentSource;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="ICustomWorkflowCatalog"/> 默认实现：用 <see cref="INoteService"/> 持久化、
/// <see cref="WorkflowSourceParser"/> JSON 序列化。
/// </summary>
public sealed class CustomWorkflowCatalog : ICustomWorkflowCatalog
{
    private readonly INoteService _notes;

    public CustomWorkflowCatalog(INoteService notes)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
    }

    public async Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default)
    {
        var all = await _notes.GetAllAsync(ct);
        var result = new List<CustomWorkflowDefinition>();
        foreach (var note in all.Where(n => n.NodeType == NoteNodeType.Workflow))
        {
            if (WorkflowSourceParser.TryParse(note.Content, out var def, out _) && def is not null)
            {
                def.Id = Guid.TryParse(note.Id, out var gid) ? gid : Guid.NewGuid();
                result.Add(def);
            }
        }
        return result;
    }

    public async Task<CustomWorkflowDefinition?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var note = await _notes.GetAsync(id.ToString(), ct);
        if (note is null || note.NodeType != NoteNodeType.Workflow) return null;
        if (!WorkflowSourceParser.TryParse(note.Content, out var def, out _) || def is null) return null;
        def.Id = id;
        return def;
    }

    public async Task SaveAsync(CustomWorkflowDefinition definition, CancellationToken ct = default)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("名称不能为空", nameof(definition));

        var source = WorkflowSourceParser.Serialize(definition);

        var existing = await _notes.GetAsync(definition.Id.ToString(), ct);
        if (existing is null)
        {
            await _notes.CreateAsync(new CreateNoteRequest
            {
                Title = definition.Name,
                Content = source,
                NodeType = NoteNodeType.Workflow,
            }, ct);
        }
        else
        {
            await _notes.UpdateAsync(new UpdateNoteRequest
            {
                Id = definition.Id.ToString(),
                Title = definition.Name,
                Content = source,
                NodeType = NoteNodeType.Workflow,
            }, ct);
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        => await _notes.DeleteAsync(id.ToString(), ct);
}
