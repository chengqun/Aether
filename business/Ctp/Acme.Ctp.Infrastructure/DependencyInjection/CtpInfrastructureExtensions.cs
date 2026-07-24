using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the CTP infrastructure layer. Registers the CTP session
/// (the connection to the CTP broker). <c>CtpSessionService</c> is the concrete
/// session used by the AI tools (it exposes native SDK methods); <c>ICtpSession</c>
/// is the DTO-surface contract used by Application. Both point at the same
/// singleton so login/session state is shared.
/// </summary>
public static class CtpInfrastructureExtensions
{
    public static IServiceCollection AddCtpInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<CtpSessionService>();
        services.AddSingleton<ICtpSession>(sp => sp.GetRequiredService<CtpSessionService>());
        return services;
    }
}
