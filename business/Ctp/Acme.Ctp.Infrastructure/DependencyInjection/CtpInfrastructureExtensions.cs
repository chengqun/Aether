using System.IO;
using Acme.Ctp.Domain;
using Acme.Ctp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Ctp.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the CTP infrastructure layer. Registers the CTP session
/// (the connection to the CTP broker) + the SQLite config store for credentials.
/// </summary>
public static class CtpInfrastructureExtensions
{
    /// <param name="sqliteDbPath">SQLite file for the ctp_config table. Falls back to <c>CTP_DB_PATH</c>, then <c>~/.aether/ctp.db</c>.</param>
    public static IServiceCollection AddCtpInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("CTP_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "ctp.db");

        services.AddSingleton<CtpSessionService>();
        services.AddSingleton<ICtpSession>(sp => sp.GetRequiredService<CtpSessionService>());
        services.AddSingleton<ICtpConfigStore>(_ => SqliteCtpConfigStore.ForFile(dbPath));
        return services;
    }
}
