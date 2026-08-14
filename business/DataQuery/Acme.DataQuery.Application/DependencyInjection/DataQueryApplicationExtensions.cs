using Microsoft.Extensions.DependencyInjection;

namespace Acme.DataQuery.Application;

public static class DataQueryApplicationExtensions
{
    public static IServiceCollection AddDataQueryApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISqlQueryService, SqlQueryService>();
        return services;
    }
}
