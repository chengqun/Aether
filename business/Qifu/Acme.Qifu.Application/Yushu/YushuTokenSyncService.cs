using Acme.AppAuth.Domain;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.Yushu;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Qifu.Application.Yushu;

/// <summary>
/// 毓数平台 Token 跨端同步：桌面端上传到服务器，手机端从服务器拉取。
/// </summary>
public class YushuTokenSyncService : IYushuTokenSyncService
{
    private readonly IAppServerHttpClient _syncHttp;
    private readonly IQifuConfigCache _configRepository;
    private readonly ICurrentUser _userContext;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public YushuTokenSyncService(
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

            // 读取本地 token
            var tokenItem = await _configRepository.GetAsync(userId, "token");
            if (tokenItem == null) return false; // 本地无 token，不上传

            var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenItem.ConfigJson, JsonOptions);

            var accessToken = tokenData.TryGetProperty("access_token", out var atProp) ? atProp.GetString() ?? "" : "";
            var refreshToken = tokenData.TryGetProperty("refresh_token", out var rtProp) ? rtProp.GetString() : null;
            var expiresAt = tokenData.TryGetProperty("expires_at", out var eaProp) ? eaProp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 604800;
            var tokenType = tokenData.TryGetProperty("token_type", out var ttProp) ? ttProp.GetString() ?? "Bearer" : "Bearer";
            var clientConfigJson = await GetClientConfigJsonAsync(userId);

            // 使用 camelCase 匿名类型，匹配 ABP 服务端 SaveYushuTokenDto 的 camelCase JSON 约定
            var body = new
            {
                accessToken,
                refreshToken,
                expiresAt,
                tokenType,
                clientConfigJson,
            };

            var response = await _syncHttp.PostRawAsync("api/app/yushu-token/save-my-token", body);

            if (response == null)
            {
                System.Diagnostics.Debug.WriteLine("[YushuTokenSync] 上传 token 失败：请求异常（网络错误或连接超时）");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                System.Diagnostics.Debug.WriteLine($"[YushuTokenSync] 上传 token 失败：HTTP {statusCode}，响应：{errorContent}");
                return false;
            }

            System.Diagnostics.Debug.WriteLine("[YushuTokenSync] Token 已成功上传到服务器");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YushuTokenSync] 上传 token 异常：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SyncFromServerAsync(CancellationToken ct = default)
    {
        var userId = await _userContext.GetCurrentUserIdAsync(ct) ?? throw new InvalidOperationException("No current qifu user context.");

        try
        {
            var result = await _syncHttp.GetAsync<YushuTokenServerDto>("api/app/yushu-token/my-token");
            if (result == null || !result.HasToken || result.IsExpired || string.IsNullOrEmpty(result.AccessToken))
            {
                return false; // 服务器无有效 token
            }

            // 保存到本地 SQLite
            var expiresAt = result.ExpiresAt;
            var tokenDataToSave = new Dictionary<string, object>
            {
                ["access_token"] = result.AccessToken,
                ["expires_at"] = expiresAt,
                ["token_type"] = result.TokenType ?? "Bearer",
            };

            await _configRepository.UpsertAsync(new QifuConfigCacheItem
            {
                UserId = userId,
                ConfigKey = "token",
                ConfigJson = JsonSerializer.Serialize(tokenDataToSave, JsonOptions),
                CachedAt = DateTime.UtcNow,
            });

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[YushuTokenSync] 从服务器拉取 token 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取本地 OAuth client 配置 JSON（用于上传到服务器）
    /// </summary>
    private async Task<string?> GetClientConfigJsonAsync(string userId)
    {
        var clientConfigItem = await _configRepository.GetAsync(userId, "client_config");
        return clientConfigItem?.ConfigJson;
    }

    /// <summary>
    /// 服务器返回的 Token DTO（用于反序列化）
    /// </summary>
    private class YushuTokenServerDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public long ExpiresAt { get; set; }
        public string TokenType { get; set; } = "Bearer";
        public bool HasToken { get; set; }
        public bool IsExpired { get; set; }
    }

}
