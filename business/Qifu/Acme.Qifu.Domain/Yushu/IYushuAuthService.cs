namespace Acme.Qifu.Domain.Yushu;

/// <summary>
/// 毓数平台 OAuth2 认证服务，管理与 Python MCP server 共享的 token 缓存。
/// </summary>
public interface IYushuAuthService
{
    /// <summary>获取有效的访问令牌。如果 token 过期或不存在，将触发浏览器登录流程。</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>检查本地是否已有有效的毓数平台访问令牌。</summary>
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    /// <summary>主动发起毓数平台认证（桌面端走浏览器 OAuth，移动端从服务器同步 token）。返回 accessToken，失败返回 null。</summary>
    Task<string?> LoginAsync(CancellationToken ct = default);

    /// <summary>清除本地 token 缓存并重新认证（401 恢复用）。返回新的 accessToken。</summary>
    Task<string> ReAuthenticateAsync(CancellationToken ct = default);
}
