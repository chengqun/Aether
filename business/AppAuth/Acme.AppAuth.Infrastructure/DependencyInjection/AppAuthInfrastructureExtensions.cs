using System.IO;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Infrastructure.Auth;
using Acme.AppAuth.Infrastructure.Http;
using Acme.AppAuth.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.AppAuth.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the Platform (app-server foundation) context. Registers
/// the session token cache (SQLite), the authenticated app-server HTTP client
/// (with bearer auto-attach + refresh-on-401), and the current-user resolver.
/// Other bounded contexts (qifu, …) consume <see cref="IAppServerHttpClient"/>
/// and <see cref="ICurrentUser"/> from here.
/// </summary>
public static class PlatformInfrastructureExtensions
{
    public static IServiceCollection AddAppAuthInfrastructure(
        this IServiceCollection services,
        Action<AppServerOptions>? configure = null)
    {
        var options = new AppServerOptions();
        configure?.Invoke(options);

        // Env-var fallbacks.
        if (string.IsNullOrEmpty(options.BaseUrl))
            options.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL") ?? string.Empty;
        if (string.IsNullOrEmpty(options.ClientId))
            options.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? string.Empty;
        if (string.IsNullOrEmpty(options.ClientSecret))
            options.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? string.Empty;
        if (string.IsNullOrEmpty(options.DbPath))
            options.DbPath = Environment.GetEnvironmentVariable("PLATFORM_DB_PATH");

        services.AddSingleton(options);

        // Session token cache (SQLite, shared file).
        var dbPath = options.DbPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "platform.db");
        services.AddSingleton<IAppTokenStore>(_ => SqliteAppTokenStore.ForFile(dbPath));

        // Authenticated app-server HTTP client: tenant header + bearer handler + base address.
        services.AddTransient<TenantHeaderDelegatingHandler>();
        services.AddTransient<BearerTokenDelegatingHandler>();
        services.AddHttpClient<IAppServerHttpClient, AppServerHttpClient>(c =>
        {
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                c.BaseAddress = new Uri(options.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(300);
        })
        .AddHttpMessageHandler<TenantHeaderDelegatingHandler>()
        .AddHttpMessageHandler<BearerTokenDelegatingHandler>();

        // Current user (from cached JWT).
        services.AddSingleton<ICurrentUser, CurrentUserResolver>();

        return services;
    }
}
