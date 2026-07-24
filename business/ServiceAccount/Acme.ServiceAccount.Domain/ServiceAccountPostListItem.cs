using System;

namespace Acme.ServiceAccount.Domain;

public class ServiceAccountPostListItem
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

    public string ContentTypeDisplayName => ContentType switch
    {
        "Article" => "文章",
        "Analysis" => "分析",
        "Alert" => "提醒",
        "Link" => "链接",
        _ => ContentType
    };
}