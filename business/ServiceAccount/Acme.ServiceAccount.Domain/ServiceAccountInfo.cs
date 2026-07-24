using System;

namespace Acme.ServiceAccount.Domain;

public class ServiceAccountInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public string? WelcomeMessage { get; set; }
    public DateTime CreationTime { get; set; }
    public int FollowerCount { get; set; }
    public bool IsFollowedByCurrentUser { get; set; }
}