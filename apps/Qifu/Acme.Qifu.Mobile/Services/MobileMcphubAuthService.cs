using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.Mobile.Services;

/// <summary>
/// 手机端 mcphub 网关 JWT 鉴权适配器：同时实现两个 <c>IMcphubAuthService</c> 接口（Domain 层端口 + Mvvm 层 UI 端口，
/// 两者 <c>IsAuthenticatedAsync</c>/<c>LoginAsync</c> 签名一致）。
/// 手机端无法自行跑浏览器 OAuth，JWT 来源 = 从服务器拉取桌面端已上传的 JWT（<see cref="IMcphubTokenSyncService.SyncFromServerAsync"/>）。
/// 区别于 <see cref="McphubAuthService"/>（桌面，浏览器 OAuth + 落本地 credentials.json）；本实现只缓存内存 JWT，
/// 失效则重拉服务器（若服务器也无则让调用方感知失败，引导用户在桌面端重新登录）。
/// Domain 端口供 <c>YushuService</c>（SQL 查询工具）用；Mvvm 端口供 AI 设置页 mcphub 卡「从服务器同步」用。
/// </summary>
internal sealed class MobileMcphubAuthService : IMcphubAuthService, Acme.Mobile.Mvvm.Services.IMcphubAuthService
{
    private readonly IMcphubTokenSyncService _tokenSyncService;

    private string? _cachedToken;
    private readonly object _gate = new();

    public MobileMcphubAuthService(IMcphubTokenSyncService tokenSyncService)
    {
        _tokenSyncService = tokenSyncService;
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cachedToken is not null) return _cachedToken;
        }

        var jwt = await _tokenSyncService.SyncFromServerAsync(ct);
        if (string.IsNullOrEmpty(jwt))
        {
            throw new InvalidOperationException("服务器无 mcphub 凭据，请在桌面端「连接器」完成 mcphub 登录");
        }

        lock (_gate) _cachedToken = jwt;
        return jwt;
    }

    /// <inheritdoc />
    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        try
        {
            _ = await GetAccessTokenAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        // 手机端登录 = 从服务器拉 JWT（无浏览器）。缓存置空后重拉。
        lock (_gate) _cachedToken = null;
        try
        {
            return await GetAccessTokenAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> ReAuthenticateAsync(CancellationToken ct = default)
    {
        // 401 恢复：清缓存重拉。若服务器 JWT 本身失效，仍会失败——需桌面端重新登录。
        lock (_gate) _cachedToken = null;
        return await GetAccessTokenAsync(ct);
    }
}
