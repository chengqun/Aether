using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Infrastructure.Http;

/// <summary>
/// Attaches the ABP <c>__tenant</c> header to every outgoing app-server request
/// (mirrors AlphaAgent's TenantHeaderDelegatingHandler). Without it, ABP can't
/// resolve the user's tenant from a token-only request and returns 401 / wrong
/// tenant data. Registered on <see cref="IAppServerHttpClient"/> ahead of the
/// bearer handler.
/// </summary>
public sealed class TenantHeaderDelegatingHandler : DelegatingHandler
{
    private readonly AppServerOptions _options;

    // AppServerOptions is registered as a plain singleton (not via IOptions),
    // so inject it directly to read the configured Tenant.
    public TenantHeaderDelegatingHandler(AppServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tenant = _options.Tenant;
        if (!string.IsNullOrWhiteSpace(tenant) && !request.Headers.Contains("__tenant"))
            request.Headers.Add("__tenant", tenant);

        return base.SendAsync(request, cancellationToken);
    }
}
