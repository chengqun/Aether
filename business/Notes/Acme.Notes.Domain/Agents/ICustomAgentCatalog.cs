namespace Acme.Notes.Domain.Agents;

/// <summary>
/// 自定义 Agent 目录服务：基于 Notes（<c>NoteNodeType.Agent</c>）做 CRUD，
/// 并暴露可用工具池供编辑页勾选。UI 无关，可被任意 UI 框架的 ViewModel 复用。
/// 契约住 Notes.Domain（与它返回的 <see cref="CustomAgentDefinition"/> 同居），impl 在 ai/Agents 适配器
///（桥接 ToolRegistry/SkillRegistry）——同 business-port 范式（port 在 Domain、impl 在宿主/适配器）。
/// </summary>
public interface ICustomAgentCatalog
{
    /// <summary>列出全部自定义 Agent（无法解析源格式的条目被跳过）。</summary>
    Task<IReadOnlyList<CustomAgentDefinition>> ListAsync(CancellationToken ct = default);

    /// <summary>按 id 取单个 Agent；不存在返回 null。</summary>
    Task<CustomAgentDefinition?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>按市场云端 Id 取本地副本（已召唤的市场 agent）；不存在返回 null。</summary>
    Task<CustomAgentDefinition?> GetByMarketplaceIdAsync(Guid marketplaceId, CancellationToken ct = default);

    /// <summary>新增或更新（按 id 是否已存在判定）。</summary>
    Task SaveAsync(CustomAgentDefinition definition, CancellationToken ct = default);

    /// <summary>按 id 删除。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>当前注册到 ToolRegistry 的全部工具元数据（供编辑页勾选）。
    /// 含「当前平台不可用」的工具（<see cref="CustomToolMeta.HostCapability"/> ≠ <see cref="CurrentHost"/>），
    /// 由编辑 VM 置灰而非过滤——用户可见全貌 + 知道为何不可选。</summary>
    IReadOnlyList<CustomToolMeta> GetAvailableTools();

    /// <summary>当前宿主类型名："All"/"DesktopOnly"/"MobileOnly"（供编辑页判定工具可勾选性）。
    /// 与 <see cref="CustomToolMeta.HostCapability"/> 同 string 形状（business 不引 Aether.AI.* 契约）。</summary>
    string CurrentHost { get; }

    /// <summary>可选模型列表（供编辑页下拉，由宿主 IModelSource 提供）。</summary>
    IReadOnlyList<CustomModelOption> GetAvailableModels();
}
