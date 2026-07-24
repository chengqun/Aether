using Acme.Moments.Application;
using Acme.Moments.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Moments.Application.DependencyInjection;

public static class MomentsApplicationExtensions
{
    public static IServiceCollection AddMomentsApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMomentService, MomentService>();
        return services;
    }
}