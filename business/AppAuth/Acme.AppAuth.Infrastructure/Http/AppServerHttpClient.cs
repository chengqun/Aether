using System.Net.Http;
using System.Text;
using System.Text.Json;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Infrastructure.Http;

/// <summary>
/// Authenticated HTTP client for the company app server. Wraps an
/// <see cref="HttpClient"/> whose handler chain includes
/// <see cref="BearerTokenDelegatingHandler"/> (so requests carry the bearer
/// token and auto-refresh on 401). 请求 URI 按 <see cref="AppServerOptions.BaseUrl"/>
/// 拼绝对路径——不依赖 <c>HttpClient.BaseAddress</c>（单例 typed client 首次请求后
/// BaseAddress 不可变；登录页 <c>ConfigureBaseUrl</c> 改了 BaseUrl 后，本 client
/// 下一次请求即时走新地址，无需重建 client）。
/// Transport failures return null, matching qifu's expectation.
/// </summary>
public sealed class AppServerHttpClient : IAppServerHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly AppServerOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppServerHttpClient(HttpClient httpClient, AppServerOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>把相对端点拼成绝对 URI（基于 _options.BaseUrl）；已是绝对 URI 则原样返回。
    /// 无 BaseUrl 时返回原端点（调用会失败抛 → 各方法 catch 返回 null，调用方降级）。</summary>
    private string BuildRequestUri(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return endpoint;
        var baseUri = (_options.BaseUrl ?? string.Empty).TrimEnd('/');
        return string.IsNullOrEmpty(baseUri) ? endpoint : $"{baseUri}/{endpoint.TrimStart('/')}";
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage?> GetRawAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(endpoint));
            return await _httpClient.SendAsync(request, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage?> DeleteRawAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, BuildRequestUri(endpoint));
            return await _httpClient.SendAsync(request, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage?> PostRawAsync(string endpoint, object? body = null, CancellationToken ct = default)
    {
        try
        {
            var json = body is null ? "" : JsonSerializer.Serialize(body, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(endpoint))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return await _httpClient.SendAsync(request, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default) where T : class
    {
        try
        {
            using var response = await _httpClient.GetAsync(BuildRequestUri(endpoint), ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken ct = default) where T : class
    {
        try
        {
            var json = body is null ? "" : JsonSerializer.Serialize(body, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(endpoint))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(responseJson, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage?> PostFormRawAsync(string endpoint, IDictionary<string, string> form, CancellationToken ct = default)
    {
        try
        {
            return await _httpClient.PostAsync(BuildRequestUri(endpoint), new FormUrlEncodedContent(form), ct);
        }
        catch
        {
            return null;
        }
    }
}
