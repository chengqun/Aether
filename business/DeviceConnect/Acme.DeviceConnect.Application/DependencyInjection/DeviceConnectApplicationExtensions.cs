using Acme.DeviceConnect.Domain;
using Acme.DeviceConnect.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceConnect.Application.DependencyInjection;

public static class DeviceConnectApplicationExtensions
{
    public static IServiceCollection AddDeviceConnectApplication(this IServiceCollection services)
    {
        services.AddDeviceConnectInfrastructure();
        services.AddSingleton<IDeviceChatClient, DeviceChatService>();
        services.AddSingleton<DeviceConfig>();
        return services;
    }
}