using System.Net.Http;
using System.Text;
using System.Text.Json;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Infrastructure.Http;

/// <summary>
/// Authenticated HTTP client for the company app server. Wraps an
/// <see cref="HttpClient"/> whose handler chain includes
/// <see cref="BearerTokenDelegatingHandler"/> (so requests carry the bearer
/// token and auto-refresh on 401) and whose base address is the server URL.
/// Transport failures return null, matching qifu's expectation.
/// </summary>
public sealed class AppServerHttpClient : IAppServerHttpClient
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppServerHttpClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc/>
    public async Task<HttpResponseMessage?> GetRawAsync(string endpoint, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
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
            using var response = await _httpClient.GetAsync(endpoint, ct);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
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
            return await _httpClient.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        }
        catch
        {
            return null;
        }
    }
}
