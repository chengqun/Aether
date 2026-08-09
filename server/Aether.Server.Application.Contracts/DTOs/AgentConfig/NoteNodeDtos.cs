using System;
using System.Collections.Generic;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Application.Contracts.DTOs.AgentConfig;

/// <summary>
/// NoteNode DTO（Blazor 管理页 CRUD 用）。镜像 AppNoteNode 实体。
/// </summary>
public class NoteNodeDto : EntityDto<Guid>
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? CreatorId { get; set; }
    public string? CreatorUserName { get; set; }
    public string NodeType { get; set; } = "Agent";
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    public string? MinAppVersion { get; set; }
    public DateTime? LastModifiedAt { get; set; }
}

public class NoteNodeCreateDto
{
    public string NodeType { get; set; } = "Agent";
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    public string? MinAppVersion { get; set; }
}

public class NoteNodeUpdateDto
{
    public string NodeType { get; set; } = "Agent";
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "普通对话";
    public string? MinAppVersion { get; set; }
}
