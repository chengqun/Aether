using Acme.DeviceManagement.Application;
using Acme.DeviceManagement.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceManagement.Application.DependencyInjection;

public static class DeviceManagementApplicationExtensions
{
    public static IServiceCollection AddDeviceManagementApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceService, DeviceService>();
        return services;
    }
}