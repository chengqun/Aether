namespace Acme.Ctp.Domain;

/// <summary>
/// CTP broker connection credentials (trading + market-data front addresses).
/// Persisted to local SQLite so the user enters them once; the host loads them
/// and connects via <c>CtpSessionService.LoginAsync</c>. Mirrors AlphaAgent's
/// stockapp storing CTP creds in DeviceConfigCache.
/// </summary>
public sealed class CtpConnectionConfig
{
    /// <summary>Trading front address (tcp://...).</summary>
    public string FrontAddress { get; set; } = string.Empty;

    /// <summary>Market-data front address (tcp://...); optional.</summary>
    public string MdFrontAddress { get; set; } = string.Empty;

    public string BrokerId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string AuthCode { get; set; } = string.Empty;

    /// <summary>启动时是否自动连接（跨重启保留）。</summary>
    public bool AutoLogin { get; set; }
}
