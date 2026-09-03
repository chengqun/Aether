namespace Acme.Qifu.Domain.Yushu;

/// <summary>
/// mcphub 网关鉴权服务：管理与 Python skill / mcphub 插件共享的 dashboard JWT 凭据
/// （<c>~/.local/share/mcphub/credentials.json</c>）。token 走 <c>x-auth-token</c> 头，
/// 有效期约 7 天。SQL 查询（<c>boltv2-*</c>）与权限工单（<c>dcpv2-*</c>）工具经 mcphub 网关统一用此凭据。
/// </summary>
public interface IMcphubAuthService
{
    /// <summary>获取有效的 mcphub JWT。缓存有效则直接返回；过期/缺失则触发浏览器 OAuth 登录。</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>本地是否已有有效的 mcphub JWT（校验 <c>/api/auth/user</c>）。</summary>
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    /// <summary>主动发起 mcphub 浏览器 OAuth 登录（loopback 回调）。返回 JWT，失败返回 null。</summary>
    Task<string?> LoginAsync(CancellationToken ct = default);

    /// <summary>清除本地 JWT 缓存并重新登录（401 恢复用）。返回新的 JWT。</summary>
    Task<string> ReAuthenticateAsync(CancellationToken ct = default);
}
