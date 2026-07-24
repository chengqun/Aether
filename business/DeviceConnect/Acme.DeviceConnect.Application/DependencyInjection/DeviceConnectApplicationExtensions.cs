using Acme.DeviceConnect.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceConnect.Application.DependencyInjection;

public static class DeviceConnectApplicationExtensions
{
    public static IServiceCollection AddDeviceConnectApplication(this IServiceCollection services)
    {
        services.AddSingleton<DeviceConfig>();
        return services;
    }
}