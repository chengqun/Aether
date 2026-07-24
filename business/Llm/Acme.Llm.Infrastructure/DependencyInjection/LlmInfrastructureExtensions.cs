using System.IO;
using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Llm.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the Llm persistence layer. Registers the SQLite
/// <see cref="ILlmConfigRepository"/>. Sync/auth come from the Platform context
/// (call <c>AddPlatformInfrastructure</c> too); the application service is
/// registered by <c>AddLlmApplication</c>.
/// </summary>
public static class LlmInfrastructureExtensions
{
    /// <param name="sqliteDbPath">SQLite file for the llm_config table. Falls back to <c>LLM_DB_PATH</c>, then <c>~/.aether/llm.db</c>.</param>
    public static IServiceCollection AddLlmInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("LLM_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "llm.db");
        services.AddSingleton<ILlmConfigRepository>(_ => SqliteLlmConfigRepository.ForFile(dbPath));
        return services;
    }
}
