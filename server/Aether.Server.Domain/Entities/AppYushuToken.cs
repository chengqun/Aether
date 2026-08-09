using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 毓数平台 OAuth2 Token 存储，用于跨端同步（桌面端认证后上传，手机端拉取使用）。
/// </summary>
public class AppYushuToken : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// 用户 ID（每用户仅一条记录）
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 访问令牌
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// 刷新令牌
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Token 过期时间（Unix 秒）
    /// </summary>
    public long ExpiresAt { get; set; }

    /// <summary>
    /// Token 类型（默认 Bearer）
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// OAuth 客户端配置 JSON（clientId/clientSecret，用于手机端换 token）
    /// </summary>
    public string? ClientConfigJson { get; set; }

    protected AppYushuToken() { }

    public AppYushuToken(
        Guid id,
        Guid userId,
        string accessToken,
        string? refreshToken,
        long expiresAt,
        string tokenType = "Bearer",
        string? clientConfigJson = null) : base(id)
    {
        UserId = userId;
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        ExpiresAt = expiresAt;
        TokenType = tokenType;
        ClientConfigJson = clientConfigJson;
    }
}
