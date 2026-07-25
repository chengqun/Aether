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
        // IAgentExecutor 由 AI 适配器（ai/Automation）注册：业务 Infrastructure 不依赖 AI 平台。
        // 组合 Automation 时需同时调 AddAutomationInfrastructure() + AddAutomationAgentExecutor()。
        return services;
    }
}