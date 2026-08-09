using System;

namespace Aether.Server.Application.Contracts.DTOs.AgentUsage;

/// <summary>
/// 客户端上报 Agent 使用量 DTO
/// </summary>
public class RecordAgentUsageDto
{
    public string AgentName { get; set; } = string.Empty;
    public int AgentKind { get; set; }
    public Guid SessionId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
}

/// <summary>
/// Agent 使用汇总 DTO（按 AgentName 分组）
/// </summary>
public class AgentUsageSummaryDto
{
    public string AgentName { get; set; } = string.Empty;
    public int AgentKind { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public int CallCount { get; set; }
}

/// <summary>
/// Agent 使用明细 DTO
/// </summary>
public class AgentUsageDetailDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public int AgentKind { get; set; }
    public Guid SessionId { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public DateTime CreationTime { get; set; }
}