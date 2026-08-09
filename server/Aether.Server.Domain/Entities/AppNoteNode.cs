using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 服务端 NoteNode：Note 相关表，存市场发布的 Agent/Workflow（源文本 front-matter + Markdown 为单一真相源）。
/// 替代旧 AppAgentConfig。NodeType: "Agent"/"Workflow"（市场）/ "Note"（普通笔记，预留）。
/// IsPublished=true 的 Agent/Workflow 进市场列表。多租户：host 级 TenantId=null 全租户可见。
/// </summary>
public class AppNoteNode : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>多租户：所属租户（host 级为 null，全租户共享）。ABP 自动按当前租户过滤 + 写入。</summary>
    public Guid? TenantId { get; set; }

    /// <summary>所属用户（发布者）。市场按 CreatorId 鉴权发布/下架。</summary>
    public Guid UserId { get; set; }

    /// <summary>节点类型："Agent"/"Workflow"/"Note"。市场仅发布 Agent/Workflow。</summary>
    public string NodeType { get; set; } = "Agent";

    /// <summary>标题 = Agent/Workflow 名（市场展示 + 召唤落本地 NoteNode.Title）。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>源文本（YAML front-matter + Markdown 正文），agent/workflow 的单一真相源。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否发布到市场（公共市场，登录用户可召唤）。</summary>
    public bool IsPublished { get; set; } = false;

    /// <summary>描述（市场列表展示，从 front-matter 同步冗余）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>分类（市场列表展示）。</summary>
    public string Category { get; set; } = "普通对话";

    /// <summary>调用所需最低 app 版本号（客户端召唤前校验）。</summary>
    public string? MinAppVersion { get; set; }

    public AppNoteNode() { }

    public AppNoteNode(string title, string content, string nodeType = "Agent")
    {
        Title = title;
        Content = content;
        NodeType = nodeType;
    }
}
