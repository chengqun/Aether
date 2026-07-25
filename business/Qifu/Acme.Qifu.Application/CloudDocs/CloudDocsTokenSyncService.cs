using Acme.AppAuth.Domain;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Ports;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Qifu.Application.CloudDocs;

/// <summary>
/// 360Teams 云文档 sid 跨端同步：桌面端上传到服务器，手机端从服务器拉取。
/// 镜像 YushuTokenSyncService。
/// </summary>
public class CloudDocsTokenSyncService : ICloudDocsTokenSyncService
{
    private const string SidConfigKey = "sid";

    private readonly IAppServerHttpClient _syncHttp;
    private readonly IQifuConfigCache _configRepository;
    private readonly ICurrentUser _userContext;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CloudDocsTokenSyncService(
        IAppServerHttpClient syncHttp,
        IQifuConfigCache configCache,
        ICurrentUser userContext)
    {
        _syncHttp = syncHttp;
        _configRepository = configCache;
        _userContext = userContext;
    }

    /// <inheritdoc />
    public async Task<bool> SyncToServerAsync(CancellationToken ct = default)
    {
        try
        {
            var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");

            // 读取本地 sid
            var sidItem = await _configRepository.GetAsync(userId, SidConfigKey);
            if (sidItem == null) return false; // 本地无 sid，不上传

            string sid;
            string env = "prod";
            string? capturedAt = null;
            try
            {
                using var doc = JsonDocument.Parse(sidItem.ConfigJson);
                sid = doc.RootElement.TryGetProperty("sid", out var s) ? s.GetString() ?? "" : "";
                if (doc.RootElement.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.String)
                    env = e.GetString() ?? "prod";
                if (doc.RootElement.TryGetProperty("captured_at", out var c) && c.ValueKind == JsonValueKind.String)
                    capturedAt = c.GetString();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(sid)) return false;

            // 使用 camelCase 匿名类型，匹配服务端 SaveCloudDocsTokenDto 的 camelCase JSON 约定
            var body = new
            {
                sid,
                env,
                capturedAt,
            };

            var response = await _syncHttp.PostRawAsync("api/app/cloud-docs-token/save-my-token", body);

            if (response == null)
            {
                System.Diagnostics.Debug.WriteLine("[CloudDocsTokenSync] 上传 sid 失败：请求异常（网络错误或连接超时）");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                System.Diagnostics.Debug.WriteLine($"[CloudDocsTokenSync] 上传 sid 失败：HTTP {statusCode}，响应：{errorContent}");
                return false;
            }

            System.Diagnostics.Debug.WriteLine("[CloudDocsTokenSync] sid 已成功上传到服务器");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudDocsTokenSync] 上传 sid 异常：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SyncFromServerAsync(CancellationToken ct = default)
    {
        var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");

        try
        {
            var result = await _syncHttp.GetAsync<CloudDocsTokenServerDto>("api/app/cloud-docs-token/my-token");
            if (result == null || !result.HasToken || string.IsNullOrEmpty(result.Sid))
            {
                return false; // 服务器无 sid
            }

            // 保存到本地 SQLite（与 CloudDocsAuthService.StoreSidAsync 相同的 JSON 结构）
            var dataToSave = new Dictionary<string, object>
            {
                ["sid"] = result.Sid,
                ["env"] = string.IsNullOrEmpty(result.Env) ? "prod" : result.Env,
                ["captured_at"] = result.CapturedAt ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };

            await _configRepository.UpsertAsync(new QifuConfigCacheItem
            {
                UserId = userId,
                ConfigKey = SidConfigKey,
                ConfigJson = JsonSerializer.Serialize(dataToSave, JsonOptions),
                CachedAt = DateTime.UtcNow,
            });

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CloudDocsTokenSync] 从服务器拉取 sid 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>服务端返回的 sid DTO（用于反序列化）</summary>
    private class CloudDocsTokenServerDto
    {
        public string Sid { get; set; } = string.Empty;
        public string Env { get; set; } = "prod";
        public string? CapturedAt { get; set; }
        public bool HasToken { get; set; }
    }
}
