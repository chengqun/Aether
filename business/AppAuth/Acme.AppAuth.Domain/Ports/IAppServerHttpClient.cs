using System.Net.Http;

namespace Acme.AppAuth.Domain;

/// <summary>
/// Authenticated HTTP client for the company app server (ABP). Outgoing
/// requests automatically carry the current user's bearer token (via a
/// delegating handler); 401 responses trigger a token refresh + retry.
/// Endpoints are relative to the server base address configured by the host.
/// <para>
/// This is the foundation transport that bounded contexts (qifu token-sync,
/// future contexts) consume for app-server calls.
/// </para>
/// </summary>
public interface IAppServerHttpClient
{
    /// <summary>POSTs a JSON body to a relative endpoint, returning the raw response (or null on transport failure).</summary>
    Task<HttpResponseMessage?> PostRawAsync(string endpoint, object? body = null, CancellationToken ct = default);

    /// <summary>GETs a relative endpoint, returning the raw response (or null on transport failure). Lets callers read status/body/headers for error surfacing.</summary>
    Task<HttpResponseMessage?> GetRawAsync(string endpoint, CancellationToken ct = default);

    /// <summary>GETs a relative endpoint and deserializes JSON to <typeparamref name="T"/>, or null.</summary>
    Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default) where T : class;

    /// <summary>POSTs a JSON body to a relative endpoint and deserializes JSON to <typeparamref name="T"/>, or null.</summary>
    Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken ct = default) where T : class;

    /// <summary>POSTs a form-urlencoded body (OAuth2 token endpoint use), returning the raw response.</summary>
    Task<HttpResponseMessage?> PostFormRawAsync(string endpoint, IDictionary<string, string> form, CancellationToken ct = default);
}
