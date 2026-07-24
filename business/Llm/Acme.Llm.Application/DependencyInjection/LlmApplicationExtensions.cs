using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Platform.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Llm.Application.DependencyInjection;

/// <summary>
/// DI entry point for the Llm Application service. Call alongside
/// <c>AddLlmInfrastructure</c> (and <c>AddPlatformInfrastructure</c> for sync/auth)
/// at the composition root.
/// </summary>
public static class LlmApplicationExtensions
{
    public static IServiceCollection AddLlmApplication(this IServiceCollection services)
    {
        services.AddSingleton<ILlmConfigService>(sp => new LlmConfigService(
            sp.GetRequiredService<IAppServerHttpClient>(),
            sp.GetRequiredService<ILlmConfigRepository>()));
        return services;
    }
}
