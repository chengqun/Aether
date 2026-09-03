using System.Text.Json;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.Mobile.Services;

/// <summary>
/// 手机端毓数认证适配器：实现 <see cref="Acme.Mobile.Mvvm.Services.IYushuAuthService"/>（手机 VM 层本地端口）。
/// 手机端无法自行跑浏览器 OAuth，登录=从服务器拉 token（<see cref="IYushuTokenSyncService.SyncFromServerAsync"/>），
/// 认证状态=本地缓存 token 是否仍有效（镜像桌面 <c>YushuAuthService.IsAuthenticatedAsync</c> 的 expires_at 校验）。
/// </summary>
internal sealed class MobileYushuAuthService : Acme.Mobile.Mvvm.Services.IYushuAuthService
{
    private const string TokenConfigKey = "token";
    private const long ExpirySkewSeconds = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IQifuConfigCache _configCache;
    private readonly IYushuTokenSyncService _tokenSyncService;

    public MobileYushuAuthService(IQifuConfigCache configCache, IYushuTokenSyncService tokenSyncService)
    {
        _configCache = configCache;
        _tokenSyncService = tokenSyncService;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        var tokenItem = await _configCache.GetAsync(TokenConfigKey, ct);
        if (tokenItem == null) return false;

        try
        {
            using var doc = JsonDocument.Parse(tokenItem.ConfigJson);
            var expiresAt = doc.RootElement.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.Number
                ? ea.GetInt64()
                : 0L;
            // 留 60 秒缓冲，与桌面端一致
            return expiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ExpirySkewSeconds;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> LoginAsync(CancellationToken ct = default)
    {
        // 手机端登录 = 从服务器拉 token（依赖桌面端已上传）
        var synced = await _tokenSyncService.SyncFromServerAsync(ct);
        if (!synced) return null;

        var tokenItem = await _configCache.GetAsync(TokenConfigKey, ct);
        if (tokenItem == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(tokenItem.ConfigJson);
            return doc.RootElement.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
                ? at.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
