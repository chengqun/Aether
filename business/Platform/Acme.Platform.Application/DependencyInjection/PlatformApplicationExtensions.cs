using Acme.Platform.Application.Auth;
using Acme.Platform.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Platform.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Platform Application services (auth use cases).
/// Call alongside <c>AddPlatformInfrastructure</c> at the composition root.
/// </summary>
public static class PlatformApplicationExtensions
{
    public static IServiceCollection AddPlatformApplication(this IServiceCollection services)
    {
        // AppAuthService uses a plain HttpClient (base address only, NO bearer
        // handler) for connect/token — routing login/refresh through the
        // authenticated client would cycle (handler → refresher → client → handler).
        // One instance backs both interfaces (bearer handler reaches it via
        // IAppTokenRefresher; callers via IAppAuthService).
        services.AddSingleton<AppAuthService>(sp =>
        {
            var options = sp.GetRequiredService<AppServerOptions>();
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(300)
            };
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
                client.BaseAddress = new Uri(options.BaseUrl);
            return new AppAuthService(client, sp.GetRequiredService<IAppTokenStore>(), options);
        });
        services.AddSingleton<IAppAuthService>(sp => sp.GetRequiredService<AppAuthService>());
        services.AddSingleton<IAppTokenRefresher>(sp => sp.GetRequiredService<AppAuthService>());
        return services;
    }
}
