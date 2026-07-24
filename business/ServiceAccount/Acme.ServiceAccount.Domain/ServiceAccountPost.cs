using System;

namespace Acme.ServiceAccount.Domain;

public class ServiceAccountPost
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