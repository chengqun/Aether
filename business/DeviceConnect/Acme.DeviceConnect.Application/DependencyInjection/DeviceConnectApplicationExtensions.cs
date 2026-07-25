using Acme.DeviceConnect.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceConnect.Application.DependencyInjection;

public static class DeviceConnectApplicationExtensions
{
    public static IServiceCollection AddDeviceConnectApplication(this IServiceCollection services, Action<DeviceConfig>? configure = null)
    {
        services.AddSingleton<DeviceConfig>(sp =>
        {
            var config = new DeviceConfig();
            configure?.Invoke(config);
            return config;
        });
        return services;
    }
}