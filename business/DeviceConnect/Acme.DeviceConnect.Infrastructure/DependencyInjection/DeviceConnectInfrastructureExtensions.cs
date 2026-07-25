using Acme.DeviceConnect.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceConnect.Infrastructure.DependencyInjection;

public static class DeviceConnectInfrastructureExtensions
{
    public static IServiceCollection AddDeviceConnectInfrastructure(this IServiceCollection services, string dbPath)
    {
        services.AddSingleton<SqliteDeviceConfigRepository>(sp => SqliteDeviceConfigRepository.ForFile(dbPath));
        services.AddTransient<SignalRDeviceChatClient>();
        services.AddSingleton<IDeviceChatClient>(sp => sp.GetRequiredService<SignalRDeviceChatClient>());
        return services;
    }
}