using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentUsage;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.AgentUsage;

public interface IAgentUsageAppService : IApplicationService
{
    /// <summary>
    /// 客户端上报 Agent 使用量（需 X-Publish-Token 认证）
    /// </summary>
    Task RecordUsageAsync(RecordAgentUsageDto input);

    /// <summary>
    /// 获取当前用户的 Agent 使用汇总
    /// </summary>
    Task<List<AgentUsageSummaryDto>> GetMyUsageSummaryAsync();

    /// <summary>
    /// 管理员查看所有用户的 Agent 使用明细（分页）
    /// </summary>
    Task<List<AgentUsageDetailDto>> GetUsageDetailsAsync(string? agentName = null, DateTime? startTime = null, DateTime? endTime = null);
}