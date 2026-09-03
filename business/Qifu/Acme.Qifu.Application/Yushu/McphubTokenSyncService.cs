using Acme.AppAuth.Domain;
using Acme.Qifu.Domain.Yushu;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Qifu.Application.Yushu;

/// <summary>
/// mcphub 网关 JWT 跨端同步：桌面端浏览器 OAuth 登录后上传，手机端从服务器拉取。
/// </summary>
public class McphubTokenSyncService : IMcphubTokenSyncService
{
    private readonly IAppServerHttpClient _syncHttp;
    private readonly ICurrentUser _userContext;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public McphubTokenSyncService(
        IAppServerHttpClient syncHttp,
        ICurrentUser userContext)
    {
        _syncHttp = syncHttp;
        _userContext = userContext;
    }

    /// <inheritdoc />
    public async Task<bool> SyncToServerAsync(string jwt, string? username, CancellationToken ct = default)
    {
        try
        {
            // camelCase 匿名类型，匹配 ABP 服务端 SaveMcphubTokenDto 的 camelCase JSON 约定
            var body = new { jwt, username };

            var response = await _syncHttp.PostRawAsync("api/app/mcphub-token/save-my-token", body);

            if (response == null)
                throw new InvalidOperationException("上传 mcphub JWT 失败：请求异常（网络错误或连接超时，可能未登录 App）");

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"上传 mcphub JWT 失败：HTTP {statusCode}，响应：{Truncate(errorContent, 300)}");
            }

            return true;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"上传 mcphub JWT 异常：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string?> SyncFromServerAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _syncHttp.GetAsync<McphubTokenServerDto>("api/app/mcphub-token/my-token");
            if (result == null || !result.HasToken || string.IsNullOrEmpty(result.Jwt))
            {
                return null; // 服务器无有效 JWT
            }
            return result.Jwt;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[McphubTokenSync] 从服务器拉取 JWT 失败: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

    /// <summary>
    /// 服务器返回的 mcphub JWT DTO（用于反序列化）
    /// </summary>
    private class McphubTokenServerDto
    {
        public string Jwt { get; set; } = string.Empty;
        public string? Username { get; set; }
        public long CapturedAt { get; set; }
        public bool HasToken { get; set; }
    }
}
