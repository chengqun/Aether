using Microsoft.Extensions.DependencyInjection;

namespace Acme.Moments.Infrastructure.DependencyInjection;

public static class MomentsInfrastructureExtensions
{
    public static IServiceCollection AddMomentsInfrastructure(this IServiceCollection services)
    {
        return services;
    }
}