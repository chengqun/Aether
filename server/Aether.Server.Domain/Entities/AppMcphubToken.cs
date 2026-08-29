using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// mcphub 网关 dashboard JWT 存储，用于跨端同步（桌面端浏览器 OAuth 登录后上传，手机端拉取使用）。
/// 与 <see cref="AppYushuToken"/>（毓数 lowcode OAuth token）是两套独立鉴权：SQL 查询/权限工单统一走
/// mcphub 网关（x-auth-token 头），毓数 OAuth 仅 Teams 消息等用。JWT 无明确过期字段——有效性由客户端
/// 调 <c>/api/auth/user</c> 校验，失效则提示重新登录。
/// </summary>
public class AppMcphubToken : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// 用户 ID（每用户仅一条记录）
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// mcphub dashboard JWT（x-auth-token 头的值，约 7 天有效）
    /// </summary>
    public string Jwt { get; set; } = string.Empty;

    /// <summary>
    /// 登录用户名（mcphub 账户名，诊断用）
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// 上传时间（Unix 秒，供手机端判断 JWT 新旧）
    /// </summary>
    public long CapturedAt { get; set; }

    protected AppMcphubToken() { }

    public AppMcphubToken(
        Guid id,
        Guid userId,
        string jwt,
        string? username,
        long capturedAt) : base(id)
    {
        UserId = userId;
        Jwt = jwt;
        Username = username;
        CapturedAt = capturedAt;
    }
}
