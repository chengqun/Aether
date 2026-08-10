namespace Acme.ChatSocial.Domain;

/// <summary>
/// 搜索目标（用户/群/股票/设备/服务号）——服务端 <c>api/app/relationship/search-all-targets</c> 端点返回项。
/// 镜像 Aether 服务端 <c>TargetDto</c> 契约（ABP camelCase JSON，<c>PropertyNameCaseInsensitive</c> 反序列化）。
/// </summary>
public sealed class SearchTarget
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>"User" / "Group" / "Stock" / "Device" / "ServiceAccount"</summary>
    public string Type { get; set; } = string.Empty;

    public string? Description { get; set; }

    public SearchTargetSecurityInfo? SecurityInfo { get; set; }

    public SearchTargetServiceAccountInfo? ServiceAccountInfo { get; set; }
}

/// <summary>搜索结果中股票目标的附加信息（镜像服务端 TargetSecurityInfo）。</summary>
public sealed class SearchTargetSecurityInfo
{
    public string Code { get; set; } = string.Empty;
    public string SecurityType { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string BaseCode { get; set; } = string.Empty;
}

/// <summary>搜索结果中服务号目标的附加信息（镜像服务端 TargetServiceAccountInfo）。</summary>
public sealed class SearchTargetServiceAccountInfo
{
    public string Category { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
}
