using System;

namespace Aether.Server.Application.Contracts.DTOs.YushuToken;

/// <summary>
/// 客户端上报毓数平台 Token DTO（桌面端认证成功后上传）
/// </summary>
public class SaveYushuTokenDto
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public long ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string? ClientConfigJson { get; set; }
}

/// <summary>
/// 返回给客户端的毓数 Token DTO（手机端拉取时使用，不含敏感字段）
/// </summary>
public class YushuTokenDto
{
    public string AccessToken { get; set; } = string.Empty;
    public long ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    /// <summary>
    /// 是否存在有效的 token
    /// </summary>
    public bool HasToken { get; set; }
    /// <summary>
    /// Token 是否已过期
    /// </summary>
    public bool IsExpired { get; set; }
}
