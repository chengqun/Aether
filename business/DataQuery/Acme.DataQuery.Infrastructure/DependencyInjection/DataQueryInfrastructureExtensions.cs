using Acme.DataQuery.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DataQuery.Infrastructure;

public static class DataQueryInfrastructureExtensions
{
    /// <summary>注册 MySQL 数据查询基础设施：DataQueryOptions 单例（与桌面连接器卡共享）+ ISqlExecutor→MySqlSqlExecutor。</summary>
    public static IServiceCollection AddDataQueryInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<DataQueryOptions>();
        services.AddSingleton<ISqlExecutor, MySqlSqlExecutor>();
        return services;
    }
}
