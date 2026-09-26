namespace Acme.Ctp.Domain;

/// <summary>
/// CTP broker connection credentials (trading + market-data front addresses).
/// Persisted to local SQLite as a <b>connection profile</b>（多连接档案：键 = 前置地址 + 用户号，
/// 连接即自动建档）；登录窗以标签（chip）切换，点标签 = 应用 + 直接登录。旧单行
/// ctp_config 首次启动幂等迁移成一个 last-used profile。
/// </summary>
public sealed class CtpConnectionConfig
{
    /// <summary>Profile 标识（连接即建档：键 = 前置地址 + 用户号；命中既有键则复用其 Id）。</summary>
    public Guid Id { get; set; }

    /// <summary>标签名：默认 UserId；同 UserId 不同前置时加前置区分后缀（如「12345·sim」）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Trading front address (tcp://...).</summary>
    public string FrontAddress { get; set; } = string.Empty;

    /// <summary>Market-data front address (tcp://...); optional.</summary>
    public string MdFrontAddress { get; set; } = string.Empty;

    public string BrokerId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string AuthCode { get; set; } = string.Empty;

    /// <summary>启动时是否自动连接（跨重启保留；按 profile 各自保存，启动时看 last-used profile 的此旗标）。</summary>
    public bool AutoLogin { get; set; }

    /// <summary>是否「上次使用」的 profile（全表唯一，SaveAsync 事务内维护；驱动启动自动登录）。</summary>
    public bool IsLastUsed { get; set; }
}
