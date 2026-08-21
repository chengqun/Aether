namespace Acme.Notes.Domain.Agents;

/// <summary>
/// 自定义 Workflow 目录服务：基于 Notes（<c>NoteNodeType.Workflow</c>）做 CRUD。
/// UI 无关，镜像 <see cref="ICustomAgentCatalog"/>。契约住 Notes.Domain（与它返回的
/// <see cref="CustomWorkflowDefinition"/> 同居），impl 在 ai/Agents 适配器。
/// </summary>
public interface ICustomWorkflowCatalog
{
    Task<IReadOnlyList<CustomWorkflowDefinition>> ListAsync(CancellationToken ct = default);

    Task<CustomWorkflowDefinition?> GetAsync(Guid id, CancellationToken ct = default);

    Task SaveAsync(CustomWorkflowDefinition definition, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
