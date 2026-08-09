using System;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 360Teams 云文档 sid 跨端同步存储：桌面端 Playwright 登录捕获 sid 后上传，
/// 手机端（无法自行跑浏览器登录）从服务器拉取使用。
/// </summary>
public class AppCloudDocsToken : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    /// <summary>
    /// 用户 ID（每用户仅一条记录）
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// 云文档会话 sid cookie
    /// </summary>
    public string Sid { get; set; } = string.Empty;

    /// <summary>
    /// 环境标识（prod / sit）
    /// </summary>
    public string Env { get; set; } = "prod";

    /// <summary>
    /// 桌面端捕获 sid 的时间（UTC，yyyy-MM-ddTHH:mm:ssZ）
    /// </summary>
    public string? CapturedAt { get; set; }

    protected AppCloudDocsToken() { }

    public AppCloudDocsToken(
        Guid id,
        Guid userId,
        string sid,
        string env = "prod",
        string? capturedAt = null) : base(id)
    {
        UserId = userId;
        Sid = sid;
        Env = env;
        CapturedAt = capturedAt;
    }
}
