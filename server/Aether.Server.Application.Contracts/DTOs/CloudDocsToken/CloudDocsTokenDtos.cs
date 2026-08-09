namespace Aether.Server.Application.Contracts.DTOs.CloudDocsToken;

/// <summary>
/// 客户端上报云文档 sid DTO（桌面端登录成功后上传）
/// </summary>
public class SaveCloudDocsTokenDto
{
    public string Sid { get; set; } = string.Empty;
    public string Env { get; set; } = "prod";
    public string? CapturedAt { get; set; }
}

/// <summary>
/// 返回给客户端的云文档 sid DTO（手机端拉取时使用）
/// </summary>
public class CloudDocsTokenDto
{
    public string Sid { get; set; } = string.Empty;
    public string Env { get; set; } = "prod";
    public string? CapturedAt { get; set; }

    /// <summary>是否存在 sid</summary>
    public bool HasToken { get; set; }
}
