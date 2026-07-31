using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Agents.AI;

/// <summary>
/// 自定义 Workflow 目录服务：基于 Notes（<c>NoteNodeType.Workflow</c>）做 CRUD。
/// UI 无关，镜像 <see cref="ICustomAgentCatalog"/>。
/// </summary>
public interface ICustomWorkflowCatalog
{
    Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default);

    Task<CustomWorkflowDefinition?> GetAsync(Guid id, CancellationToken ct = default);

    Task SaveAsync(CustomWorkflowDefinition definition, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
