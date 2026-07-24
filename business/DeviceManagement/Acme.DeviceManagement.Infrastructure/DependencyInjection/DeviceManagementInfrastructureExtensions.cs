using System.IO;
using Acme.DeviceManagement.Domain;
using Acme.DeviceManagement.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DeviceManagement.Infrastructure.DependencyInjection;

public static class DeviceManagementInfrastructureExtensions
{
    public static IServiceCollection AddDeviceManagementInfrastructure(this IServiceCollection services, string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("DEVICE_CONFIG_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "device.db");
        services.AddSingleton<IDeviceConfigCacheRepository>(_ => SqliteDeviceConfigCacheRepository.ForFile(dbPath));
        return services;
    }
}