using System.Text.Json;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.CloudDocs;

namespace Acme.Qifu.Mobile.Services;

/// <summary>
/// 手机端云文档认证适配器：实现 <see cref="Acme.Mobile.Mvvm.Services.ICloudDocsAuthService"/>
/// （手机 VM 层本地端口）。手机端无法跑 Playwright SSO，登录=从服务器拉 sid
/// （<see cref="ICloudDocsTokenSyncService.SyncFromServerAsync"/>），认证状态=本地缓存 sid 是否存在。
/// 注意：返回的 <see cref="Acme.Mobile.Mvvm.Services.CloudDocsLoginResult"/> 是手机 VM 层的本地类型
/// （非 <c>Acme.Qifu.Domain.CloudDocs.CloudDocsLoginResult</c>）。
/// </summary>
internal sealed class MobileCloudDocsAuthService : Acme.Mobile.Mvvm.Services.ICloudDocsAuthService
{
    private const string SidConfigKey = "sid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IQifuConfigCache _configCache;
    private readonly ICloudDocsTokenSyncService _tokenSyncService;

    public MobileCloudDocsAuthService(IQifuConfigCache configCache, ICloudDocsTokenSyncService tokenSyncService)
    {
        _configCache = configCache;
        _tokenSyncService = tokenSyncService;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        // 手机端不直连 360teams 验证 sid（那需 CloudDocsService，在含 Playwright 的 Infrastructure）；
        // 仅检查本地缓存 sid 是否存在——与手机端"从服务器同步"路径一致（sid 有效性由桌面端登录时校验）。
        var sidItem = await _configCache.GetAsync(SidConfigKey, ct);
        if (sidItem == null) return false;

        try
        {
            using var doc = JsonDocument.Parse(sidItem.ConfigJson);
            return doc.RootElement.TryGetProperty("sid", out var s) && s.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(s.GetString());
        }
        catch
        {
            return false;
        }
    }

    public async Task<Acme.Mobile.Mvvm.Services.CloudDocsLoginResult?> LoginAsync(CancellationToken ct = default)
    {
        // 手机端登录 = 从服务器拉 sid（依赖桌面端已上传）
        var synced = await _tokenSyncService.SyncFromServerAsync(ct);
        if (!synced)
        {
            return new Acme.Mobile.Mvvm.Services.CloudDocsLoginResult
            {
                Success = false,
                Error = "服务器无有效 sid"
            };
        }

        // 拉取后再校验本地 sid 是否落库
        var ok = await IsAuthenticatedAsync(ct);
        return new Acme.Mobile.Mvvm.Services.CloudDocsLoginResult
        {
            Success = ok,
            Error = ok ? null : "同步成功但 sid 为空"
        };
    }
}
