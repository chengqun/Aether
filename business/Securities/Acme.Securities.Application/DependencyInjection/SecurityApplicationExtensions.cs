using Acme.Securities.Application;
using Acme.Securities.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Securities.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Security Application services. Call alongside
/// <c>AddSecurityInfrastructure</c> (registers ISecurityRepository + ISyncMetadataStore)
/// and <c>AddAppAuthInfrastructure</c> (IAppServerHttpClient for server sync).
/// </summary>
public static class SecurityApplicationExtensions
{
    public static IServiceCollection AddSecurityApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISecurityManager, SecurityManager>();
        services.AddSingleton<ISecurityService, SecurityService>();
        services.AddSingleton<ISecurityClientSyncService, SecurityClientSyncService>();
        return services;
    }
}
