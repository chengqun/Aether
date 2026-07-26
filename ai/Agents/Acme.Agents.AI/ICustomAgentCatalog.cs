namespace Acme.Agents.AI;

/// <summary>
/// 自定义 Agent 目录服务：基于 Notes（<c>NoteNodeType.Agent</c>）做 CRUD，
/// 并暴露可用工具池供编辑页勾选。UI 无关，可被任意 UI 框架的 ViewModel 复用。
/// </summary>
public interface ICustomAgentCatalog
{
    /// <summary>列出全部自定义 Agent（无法解析源格式的条目被跳过）。</summary>
    Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default);

    /// <summary>按 id 取单个 Agent；不存在返回 null。</summary>
    Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>新增或更新（按 id 是否已存在判定）。</summary>
    Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default);

    /// <summary>按 id 删除。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>当前注册到 ToolRegistry 的全部工具元数据（供编辑页勾选）。</summary>
    IReadOnlyList<CustomToolMeta> GetAvailableTools();

    /// <summary>可选模型列表（供编辑页下拉，由宿主 IModelSource 提供）。</summary>
    IReadOnlyList<CustomModelOption> GetAvailableModels();
}
