using System.IO;
using Acme.Quotes.Domain;
using Acme.Quotes.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Quotes.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the Quotes infrastructure: 3 HTTP quote providers (Sina/East/Baidu),
/// failover, indicator calculator (Skender), and the SQLite quote cache. The analysis
/// manager (Application) is registered by <c>AddQuotesApplication</c>.
/// </summary>
public static class QuotesInfrastructureExtensions
{
    /// <param name="sqliteDbPath">SQLite file for the quote cache. Falls back to <c>QUOTES_DB_PATH</c>, then <c>~/.aether/quotes.db</c>.</param>
    public static IServiceCollection AddQuotesInfrastructure(
        this IServiceCollection services,
        string? sqliteDbPath = null)
    {
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("QUOTES_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "quotes.db");

        // HTTP quote providers (raw HttpClient via AddHttpClient<T>).
        services.AddHttpClient<SinaQuoteProvider>();
        services.AddHttpClient<EastQuoteProvider>();
        services.AddHttpClient<BaiduQuoteProvider>();

        // Each provider registered as IQuoteProvider; FailoverQuoteProvider consumes IEnumerable<IQuoteProvider>.
        services.AddSingleton<IQuoteProvider, SinaQuoteProvider>();
        services.AddSingleton<IQuoteProvider, EastQuoteProvider>();
        services.AddSingleton<IQuoteProvider, BaiduQuoteProvider>();
        services.AddSingleton<IFailoverQuoteProvider, FailoverQuoteProvider>();

        services.AddSingleton<IIndicatorCalculator, IndicatorCalculator>();
        services.AddSingleton<IQuoteRepository>(_ => SqliteQuoteRepository.ForFile(dbPath));
        return services;
    }
}
