using System.Net;
using System.Net.Http.Headers;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Infrastructure.Http;

/// <summary>
/// Attaches the current app-server bearer token to outgoing requests, and on
/// 401 refreshes the token once and retries. Auth is skipped for the OAuth2
/// <c>connect/</c> and registration endpoints (they acquire the token).
/// Ported from AlphaAgent's <c>BearerTokenDelegatingHandler</c>, adapted to the
/// Platform ports (<see cref="IAppTokenStore"/> + <see cref="IAppTokenRefresher"/>).
/// </summary>
public sealed class BearerTokenDelegatingHandler : DelegatingHandler
{
    private static readonly string[] NoAuthPrefixes = { "connect/", "api/account/register" };

    private readonly IAppTokenStore _tokenStore;
    private readonly IAppTokenRefresher _refresher;

    public BearerTokenDelegatingHandler(IAppTokenStore tokenStore, IAppTokenRefresher refresher)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsNoAuthEndpoint(request.RequestUri))
        {
            var token = await _tokenStore.GetAccessTokenAsync(cancellationToken);
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !IsNoAuthEndpoint(request.RequestUri))
        {
            var refreshed = await _refresher.RefreshAsync(cancellationToken);
            if (refreshed)
            {
                var newToken = await _tokenStore.GetAccessTokenAsync(cancellationToken);
                if (!string.IsNullOrEmpty(newToken))
                {
                    using var retry = await CloneRequestAsync(request);
                    retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                    return await base.SendAsync(retry, cancellationToken);
                }
            }
        }

        return response;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
        {
            var content = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        clone.Version = request.Version;
        return clone;
    }

    private static bool IsNoAuthEndpoint(Uri? requestUri)
    {
        if (requestUri is null) return false;
        var path = requestUri.AbsolutePath.TrimStart('/');
        return NoAuthPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
