using Acme.Moments.Domain;
using Acme.Moments.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Moments.Application.DependencyInjection;

public static class MomentsApplicationExtensions
{
    public static IServiceCollection AddMomentsApplication(this IServiceCollection services)
    {
        services.AddMomentsInfrastructure();
        services.AddSingleton<IMomentService, MomentService>();
        return services;
    }
}