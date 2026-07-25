using Acme.Automation.Domain.Interfaces;
using Acme.Automation.Infrastructure.Persistence;
using Acme.Automation.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Automation.Infrastructure.DependencyInjection;

public static class AutomationInfrastructureExtensions
{
    public static IServiceCollection AddAutomationInfrastructure(this IServiceCollection services, string sqliteDbPath)
    {
        services.AddSingleton<IAutomationTaskRepository>(_ => 
            new SqliteAutomationTaskRepository($"Data Source={sqliteDbPath}"));
        services.AddSingleton<IAutomationScheduler, AutomationScheduler>();
        services.AddSingleton<IAgentExecutor, HarnessAgentExecutor>();
        return services;
    }
}