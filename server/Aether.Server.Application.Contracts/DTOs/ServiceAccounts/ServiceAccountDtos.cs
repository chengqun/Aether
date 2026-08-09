using System;

namespace Aether.Server.Application.Contracts.DTOs.ServiceAccounts;

/// <summary>
/// 服务号 DTO
/// </summary>
public class ServiceAccountDto
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

/// <summary>
/// 创建服务号 DTO
/// </summary>
public class CreateServiceAccountDto
{
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "资讯";
}

/// <summary>
/// 更新服务号 DTO
/// </summary>
public class UpdateServiceAccountDto
{
    public string Name { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "资讯";
    public bool IsVerified { get; set; }
    public string? WelcomeMessage { get; set; }
}
