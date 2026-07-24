using System.IO;
using Acme.ServiceAccount.Domain;
using Acme.ServiceAccount.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.ServiceAccount.Infrastructure.DependencyInjection;

public static class ServiceAccountInfrastructureExtensions
{
    public static IServiceCollection AddServiceAccountInfrastructure(this IServiceCollection services, string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("SERVICE_ACCOUNT_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "service_account.db");
        services.AddSingleton<IServiceAccountPostCacheRepository>(_ => SqliteServiceAccountPostCacheRepository.ForFile(dbPath));
        return services;
    }
}