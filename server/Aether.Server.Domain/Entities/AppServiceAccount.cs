using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 服务号（公众号）实体
/// </summary>
public class AppServiceAccount : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 头像 URL
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// 简介
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 分类（资讯/投研/策略/工具）
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// 创建者（管理员）ID
    /// </summary>
    public Guid OwnerId { get; set; }

    /// <summary>
    /// 是否已认证
    /// </summary>
    public bool IsVerified { get; set; }

    /// <summary>
    /// 关注时的欢迎语
    /// </summary>
    public string? WelcomeMessage { get; set; }

    public AppServiceAccount() { }

    public AppServiceAccount(Guid id, string name, Guid ownerId, string category = "资讯")
    {
        Id = id;
        Name = name;
        OwnerId = ownerId;
        Category = category;
    }

    public void Update(string name, string? avatarUrl, string? description, string category, bool isVerified, string? welcomeMessage)
    {
        Name = name;
        AvatarUrl = avatarUrl;
        Description = description;
        Category = category;
        IsVerified = isVerified;
        WelcomeMessage = welcomeMessage;
    }
}
