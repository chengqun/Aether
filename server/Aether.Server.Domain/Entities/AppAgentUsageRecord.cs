using System;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// Agent/Workflow token 使用记录，用于统计分析和成本监控。
/// </summary>
public class AppAgentUsageRecord : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public Guid UserId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public AgentKind AgentKind { get; set; }
    public Guid SessionId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }

    protected AppAgentUsageRecord() { }

    public AppAgentUsageRecord(
        Guid id,
        Guid userId,
        string agentName,
        AgentKind agentKind,
        Guid sessionId,
        int inputTokens,
        int outputTokens,
        int totalTokens) : base(id)
    {
        UserId = userId;
        AgentName = agentName;
        AgentKind = agentKind;
        SessionId = sessionId;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        TotalTokens = totalTokens;
    }
}
