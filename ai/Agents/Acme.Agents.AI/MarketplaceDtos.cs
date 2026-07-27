using System.Text.Json.Serialization;

namespace Acme.Agents.AI;

/// <summary>市场 agent 列表项（云端返回；IsSummoned 由客户端按本地 MarketplaceId 匹配后置）。</summary>
public sealed class MarketplaceAgentDto
{
    public Guid Id { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string CreatorUserName { get; set; } = string.Empty;
    public DateTimeOffset LastModifiedAt { get; set; }
    /// <summary>"Agent" | "Workflow"。</summary>
    public string Kind { get; set; } = "Agent";

    /// <summary>客户端置：本地是否已召唤该市场 agent。</summary>
    [JsonIgnore]
    public bool IsSummoned { get; set; }
    /// <summary>客户端置：本地副本是否落后于云端（LastModifiedAt &gt; 本地 MarketplaceSyncedAt）。</summary>
    [JsonIgnore]
    public bool HasUpdate { get; set; }
}

/// <summary>市场 agent 详情（含源文本 Content = front-matter + Markdown 提示词）。</summary>
public sealed class MarketplaceAgentDetailDto
{
    public Guid Id { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTimeOffset LastModifiedAt { get; set; }
    public string Kind { get; set; } = "Agent";
    /// <summary>源文本（与本地 Agent 笔记同格式）。</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>发布本地 agent 到市场的输入。</summary>
public sealed class PublishLocalAgentInputDto
{
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string MinAppVersion { get; set; } = string.Empty;
    public string Kind { get; set; } = "Agent";
}
