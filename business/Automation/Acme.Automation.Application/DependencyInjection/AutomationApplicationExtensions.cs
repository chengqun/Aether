using Acme.Automation.Application;
using Acme.Automation.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Automation.Application.DependencyInjection;

public static class AutomationApplicationExtensions
{
    public static IServiceCollection AddAutomationApplication(this IServiceCollection services)
    {
        services.AddSingleton<IAutomationService, AutomationService>();
        return services;
    }
}