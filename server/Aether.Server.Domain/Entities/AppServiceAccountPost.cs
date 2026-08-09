using System;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 服务号发布的内容
/// </summary>
public class AppServiceAccountPost : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// 所属服务号 ID
    /// </summary>
    public Guid ServiceAccountId { get; set; }

    /// <summary>
    /// 标题
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 摘要/简介
    /// </summary>
    [MaxLength(500)]
    public string? Summary { get; set; }

    /// <summary>
    /// 正文内容（Markdown 或纯文本）
    /// </summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 封面图 URL
    /// </summary>
    [MaxLength(500)]
    public string? CoverImageUrl { get; set; }

    /// <summary>
    /// 内容类型（Article/Analysis/Alert/Link）
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string ContentType { get; set; } = "Article";

    /// <summary>
    /// 是否置顶
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 发布时间
    /// </summary>
    public DateTime PublishedAt { get; set; }

    public AppServiceAccountPost() { }

    public AppServiceAccountPost(Guid id, Guid serviceAccountId, string title, string content, string contentType = "Article")
        : base(id)
    {
        ServiceAccountId = serviceAccountId;
        Title = title;
        Content = content;
        ContentType = contentType;
        PublishedAt = DateTime.UtcNow;
    }

    public void Pin() => IsPinned = true;
    public void Unpin() => IsPinned = false;
}
