namespace Acme.Qifu.Domain.CloudDocs;

/// <summary>
/// 360Teams 云文档认证服务：通过 Playwright 驱动浏览器完成 SSO 登录并捕获 sid cookie，
/// 持久化到 token 缓存（<see cref="IQifuTokenStore"/>）。后续请求自动读取缓存的 sid，过期（401）时重登。
/// </summary>
public interface ICloudDocsAuthService
{
    /// <summary>当前环境（prod / sit）</summary>
    CloudDocsEnv Env { get; }

    /// <summary>云文档 web 基地址（如 https://doc.360teams.com）</summary>
    string WebBaseUrl { get; }

    /// <summary>获取缓存的 sid（不触发登录）。无缓存或已失效返回 null。</summary>
    Task<string?> GetSidAsync(CancellationToken ct = default);

    /// <summary>本地是否已有有效 sid（通过 /api/v1/files/quota 验证）。</summary>
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    /// <summary>打开浏览器让用户完成 SSO 登录，自动捕获并验证 sid，存入缓存。成功返回 sid。</summary>
    Task<CloudDocsLoginResult> LoginAsync(int timeoutSeconds = 300, string? channel = null, CancellationToken ct = default);

    /// <summary>清除缓存的 sid 并重新登录（API 返回 401 时调用）。</summary>
    Task<CloudDocsLoginResult> ReAuthenticateAsync(int timeoutSeconds = 300, string? channel = null, CancellationToken ct = default);

    /// <summary>手动写入 sid（粘贴方式）并验证、持久化。返回验证结果。</summary>
    Task<CloudDocsLoginResult> SetSidAsync(string sid, CancellationToken ct = default);

    /// <summary>验证一个 sid 是否有效（GET /api/v1/files/quota）。</summary>
    Task<bool> VerifySidAsync(string sid, CancellationToken ct = default);
}
