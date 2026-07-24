using Acme.DeviceManagement.Domain;
using Acme.DeviceManagement.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceManagement.Application.DependencyInjection;

public static class DeviceManagementApplicationExtensions
{
    public static IServiceCollection AddDeviceManagementApplication(this IServiceCollection services)
    {
        services.AddDeviceManagementInfrastructure();
        services.AddSingleton<IDeviceService, DeviceService>();
        return services;
    }
}