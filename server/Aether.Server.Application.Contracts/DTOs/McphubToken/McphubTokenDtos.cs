using System;

namespace Aether.Server.Application.Contracts.DTOs.McphubToken;

/// <summary>
/// 客户端上报 mcphub 网关 JWT DTO（桌面端浏览器 OAuth 登录成功后上传）
/// </summary>
public class SaveMcphubTokenDto
{
    public string Jwt { get; set; } = string.Empty;
    public string? Username { get; set; }
}

/// <summary>
/// 返回给客户端的 mcphub JWT DTO（手机端拉取时使用）
/// </summary>
public class McphubTokenDto
{
    public string Jwt { get; set; } = string.Empty;
    public string? Username { get; set; }
    /// <summary>
    /// 上传时间（Unix 秒）
    /// </summary>
    public long CapturedAt { get; set; }
    /// <summary>
    /// 是否存在有效的 token
    /// </summary>
    public bool HasToken { get; set; }
}
