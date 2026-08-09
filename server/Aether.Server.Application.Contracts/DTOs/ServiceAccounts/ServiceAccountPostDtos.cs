using System;

namespace Aether.Server.Application.Contracts.DTOs.ServiceAccounts;

/// <summary>
/// 服务号内容 DTO（完整版，含正文）
/// </summary>
public class ServiceAccountPostDto
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public string ContentType { get; set; } = "Article";
    public bool IsPinned { get; set; }
    public DateTime PublishedAt { get; set; }
    public string ServiceAccountName { get; set; } = string.Empty;
    public string? ServiceAccountAvatarUrl { get; set; }
}

/// <summary>
/// 服务号内容列表项 DTO（不含正文，用于列表展示）
/// </summary>
public class ServiceAccountPostListItemDto
{
    public Guid Id { get; set; }
    public Guid ServiceAccountId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImageUrl { get; set; }
    public string ContentType { get; set; } = "Article";
    public bool IsPinned { get; set; }
    public DateTime PublishedAt { get; set; }
    public string ServiceAccountName { get; set; } = string.Empty;
}

/// <summary>
/// 创建服务号内容 DTO
/// </summary>
public class CreateServiceAccountPostDto
{
    public Guid ServiceAccountId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public string ContentType { get; set; } = "Article";
}