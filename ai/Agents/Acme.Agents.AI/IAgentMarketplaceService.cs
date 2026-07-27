namespace Acme.Agents.AI;

/// <summary>
/// 云端 agent 市场客户端。走 AppServer 已认证 HTTP（JWT 自动注入），打 ABP 路由
/// <c>api/app/agent-marketplace/*</c>。服务端待部署；未部署时调用报错（404/网络），不崩。
/// 镜像 AlphaAgent 的 IAgentMarketplaceService。
/// </summary>
public interface IAgentMarketplaceService
{
    /// <summary>拉取市场 agent 列表。失败（未登录/服务端无响应）返回 null。</summary>
    Task<List<MarketplaceAgentDto>?> GetMarketplaceAsync(CancellationToken ct = default);

    /// <summary>取单个市场 agent 详情（含源文本）。</summary>
    Task<MarketplaceAgentDetailDto?> GetAgentDetailAsync(Guid id, CancellationToken ct = default);

    /// <summary>发布本地 agent 到市场，返回云端 Id（失败 null）。</summary>
    Task<Guid?> PublishLocalAsync(PublishLocalAgentInputDto input, CancellationToken ct = default);

    /// <summary>更新已发布的市场 agent（幂等）。</summary>
    Task<bool> SyncLocalAsync(Guid id, PublishLocalAgentInputDto input, CancellationToken ct = default);

    /// <summary>下架市场 agent。</summary>
    Task<bool> UnpublishLocalAsync(Guid id, CancellationToken ct = default);
}
