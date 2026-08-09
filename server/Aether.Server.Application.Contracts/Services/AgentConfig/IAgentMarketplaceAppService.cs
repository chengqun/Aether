using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.AgentConfig;

/// <summary>
/// Agent 市场服务：发布 Agent 到公共市场，登录用户可召唤，admin 可发布/下架。
/// 消费端（列表/详情）走 [Authorize]（任何登录用户）；发布端走 [Authorize(AgentConfigs.Manage)]（admin）。
/// </summary>
public interface IAgentMarketplaceAppService : IApplicationService
{
    /// <summary>列出市场所有已发布 Agent（登录用户）</summary>
    Task<ListResultDto<MarketplaceAgentDto>> GetMarketplaceAsync();

    /// <summary>召唤时拉取单个 Agent 完整配置（登录用户）</summary>
    Task<MarketplaceAgentDetailDto> GetAsync(Guid id);

    /// <summary>本地 Agent 发布到市场（admin，按 CreatorId+AgentName 幂等）。返回云端 Id。</summary>
    Task<Guid> PublishLocalAsync(PublishLocalAgentInputDto input);

    /// <summary>同步本地最新配置到自己发布的市场 Agent（admin，校验 CreatorId）</summary>
    Task SyncLocalAsync(Guid id, PublishLocalAgentInputDto input);

    /// <summary>下架自己发布的市场 Agent（admin，校验 CreatorId，保留记录可再发）</summary>
    Task UnpublishLocalAsync(Guid id);

    /// <summary>上架指定 AgentConfig（admin，Blazor 后台）</summary>
    Task PublishAsync(Guid id);

    /// <summary>从市场下架指定 AgentConfig（admin，Blazor 后台）</summary>
    Task UnpublishAsync(Guid id);
}
