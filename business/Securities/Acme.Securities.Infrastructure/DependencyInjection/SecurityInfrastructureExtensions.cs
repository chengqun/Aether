using System.IO;
using Acme.Securities.Domain;
using Acme.Securities.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Securities.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the Security persistence layer. Registers the SQLite
/// <see cref="ISecurityRepository"/> + <see cref="ISyncMetadataStore"/>. The
/// application services (SecurityManager, SecurityService, sync) are registered
/// by <c>AddSecurityApplication</c>.
/// </summary>
public static class SecurityInfrastructureExtensions
{
    /// <param name="sqliteDbPath">SQLite file for the security + sync-metadata tables. Falls back to <c>SECURITY_DB_PATH</c>, then <c>~/.aether/security.db</c>.</param>
    public static IServiceCollection AddSecurityInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("SECURITY_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "security.db");
        services.AddSingleton<ISecurityRepository>(_ => SqliteSecurityRepository.ForFile(dbPath));
        services.AddSingleton<ISyncMetadataStore>(_ => SqliteSyncMetadataStore.ForFile(dbPath));
        services.AddSingleton<ISyncLogger, SyncLogger>();
        return services;
    }
}
