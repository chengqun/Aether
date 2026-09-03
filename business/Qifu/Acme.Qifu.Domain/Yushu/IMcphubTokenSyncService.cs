namespace Acme.Qifu.Domain.Yushu;

/// <summary>
/// mcphub 网关 JWT 跨端同步：桌面端浏览器 OAuth 登录后上传 JWT 到服务器，手机端从服务器拉取。
/// 区别于 <see cref="IYushuTokenSyncService"/>（毓数 lowcode OAuth token，经本地 SQLite 缓存中转）：
/// mcphub JWT 由 <see cref="IMcphubAuthService"/> 直接持有，同步方法显式传 JWT 字符串，不经 SQLite。
/// </summary>
public interface IMcphubTokenSyncService
{
    /// <summary>上传 mcphub JWT 到服务器（桌面端 <c>McphubAuthService</c> 登录成功后调用）。</summary>
    Task<bool> SyncToServerAsync(string jwt, string? username, CancellationToken ct = default);

    /// <summary>从服务器拉取 mcphub JWT（手机端 <c>MobileMcphubAuthService</c> 调用）。返回 JWT，服务器无则 null。</summary>
    Task<string?> SyncFromServerAsync(CancellationToken ct = default);
}
