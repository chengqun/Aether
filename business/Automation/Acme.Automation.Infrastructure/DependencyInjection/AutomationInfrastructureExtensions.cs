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
        // IAgentExecutor 是业务端口（业务层不依赖 AI 平台），其实现由宿主在组合根注册
        // （桌面端为 ConversationBackedAgentExecutor，复用聊天会话流程）。
        return services;
    }
}