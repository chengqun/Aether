using System;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Application.Contracts.DTOs.AgentConfig;

/// <summary>
/// Agent 市场列表项（不含源文本 Content，供列表展示）。
/// 数据来自 AppNoteNode(IsPublished=true, NodeType=Agent/Workflow)。
/// </summary>
public class MarketplaceAgentDto : EntityDto<Guid>
{
    /// <summary>标题 = Agent/Workflow 名（= AppNoteNode.Title）。</summary>
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    /// <summary>最低 app 版本号（客户端召唤前校验）</summary>
    public string? MinAppVersion { get; set; }
    /// <summary>发布者用户名</summary>
    public string? CreatorUserName { get; set; }
    /// <summary>云端最后修改时间（客户端据此检测是否有更新）</summary>
    public DateTime? LastModifiedAt { get; set; }
    /// <summary>配置类型："Agent" 或 "Workflow"(专家团)。默认 Agent（= AppNoteNode.NodeType）。</summary>
    public string Kind { get; set; } = "Agent";

    /// <summary>是否已召唤到本地（客户端设置，服务端不返回）</summary>
    public bool IsSummoned { get; set; }
}

/// <summary>
/// Agent 市场详情（召唤时拉取，含源文本 Content）。
/// 客户端拿到 Content 后直接 SaveAgentSourceAsync/SaveWorkflowSourceAsync 落本地 NoteNode。
/// </summary>
public class MarketplaceAgentDetailDto : EntityDto<Guid>
{
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    /// <summary>源文本（front-matter + Markdown），agent/workflow 真相源。客户端落本地 NoteNode.Content。</summary>
    public string Content { get; set; } = string.Empty;
    public string? MinAppVersion { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    /// <summary>配置类型："Agent" 或 "Workflow"。</summary>
    public string Kind { get; set; } = "Agent";
}

/// <summary>
/// 本地 Agent/Workflow 发布到市场的输入（Note 版：直接上传源文本 Content）。
/// </summary>
public class PublishLocalAgentInputDto
{
    public string AgentName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    /// <summary>源文本（front-matter + Markdown）。发布即上传 NoteNode.Content。</summary>
    public string Content { get; set; } = string.Empty;
    public string? MinAppVersion { get; set; }
    /// <summary>配置类型："Agent"(默认) 或 "Workflow"。</summary>
    public string Kind { get; set; } = "Agent";
}
