using System.Text.Json;
using Acme.AppAuth.Domain;
using Aether.AI.Abstractions;

namespace Acme.Agents.AI;

/// <summary>
/// <see cref="IAgentMarketplaceService"/> 默认实现：通过 <see cref="IAppServerHttpClient"/> 打
/// <c>api/app/agent-marketplace/*</c>。失败时抛出带 HTTP 状态/响应体/WWW-Authenticate 的异常，供 UI 显示真实原因。
/// </summary>
public sealed class AgentMarketplaceService : IAgentMarketplaceService
{
    private readonly IAppServerHttpClient _http;
    private const string Base = "api/app/agent-marketplace";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public AgentMarketplaceService(IAppServerHttpClient http)
        => _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<List<MarketplaceAgentDto>?> GetMarketplaceAsync(CancellationToken ct = default)
    {
        // 用 GetRawAsync 拿到原始响应，便于在失败时透传 HTTP 状态/响应体/WWW-Authenticate（区分 401 原因）。
        var resp = await _http.GetRawAsync($"{Base}/marketplace", ct);
        if (resp is null)
            throw new InvalidOperationException("市场请求失败：无响应（网络错误或未登录 App）");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var wwwAuth = resp.Headers.WwwAuthenticate.ToString();
            throw new InvalidOperationException($"市场加载失败：HTTP {(int)resp.StatusCode}。{(string.IsNullOrEmpty(wwwAuth) ? "" : $"WWW-Authenticate: {wwwAuth}. ")}响应：{Truncate(body, 300)}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        // ABP 列表端点返回 ListResultDto 包裹（{ items: [...] }）
        var result = JsonSerializer.Deserialize<ListResult<MarketplaceAgentDto>>(json, JsonOpts);
        return result?.Items;
    }

    public Task<MarketplaceAgentDetailDto?> GetAgentDetailAsync(Guid id, CancellationToken ct = default)
        => _http.GetAsync<MarketplaceAgentDetailDto>($"{Base}/{id}", ct);

    public async Task<Guid?> PublishLocalAsync(PublishLocalAgentInputDto input, CancellationToken ct = default)
    {
        // 服务端返回 Guid 的 JSON 引号字符串（如 "abc-123"），用 PostRawAsync 自行解析
        var resp = await _http.PostRawAsync($"{Base}/publish-local", input, ct);
        if (resp is null || !resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var text = body?.Trim().Trim('"', '\'');
        return Guid.TryParse(text, out var id) ? id : null;
    }

    public async Task<bool> SyncLocalAsync(Guid id, PublishLocalAgentInputDto input, CancellationToken ct = default)
    {
        var resp = await _http.PostRawAsync($"{Base}/{id}/sync-local", input, ct);
        return resp is not null && resp.IsSuccessStatusCode;
    }

    public async Task<bool> UnpublishLocalAsync(Guid id, CancellationToken ct = default)
    {
        var resp = await _http.PostRawAsync($"{Base}/{id}/unpublish-local", null, ct);
        return resp is not null && resp.IsSuccessStatusCode;
    }

    /// <summary>ABP ListResultDto 包裹（{ items: [...] }）。</summary>
    private sealed class ListResult<T> { public List<T> Items { get; set; } = new(); }

    private static string Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}
