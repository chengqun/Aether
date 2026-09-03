using System.IO;
using Acme.AppAuth.Domain;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.Yushu;
using Acme.Qifu.Http.Yushu;
using Acme.Qifu.Infrastructure.CloudDocs;
using Acme.Qifu.Infrastructure.Persistence;
using Acme.Qifu.Infrastructure.Yushu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.Qifu.Infrastructure.DependencyInjection;

/// <summary>
/// DI entry point for the qifu persistence/integration layer. Registers the
/// qifu-local <see cref="IQifuConfigCache"/> (SQLite) and the four qifu service
/// implementations. The cross-cutting ports (<see cref="ICurrentUser"/>,
/// <see cref="IAppServerHttpClient"/>) are provided by the Platform context —
/// call <c>AddAppAuthInfrastructure</c> at the composition root too. The
/// token-sync Application services are registered by <c>AddQifuApplication</c>;
/// the AI adapter by <c>AddQifu</c>.
/// </summary>
public static class QifuInfrastructureExtensions
{
    /// <param name="cloudDocsEnv">CloudDocs environment (prod / sit).</param>
    /// <param name="yushuResultsDir">Local directory for Yushu SQL result files; null uses the default <c>~/.yushu/results</c>.</param>
    /// <param name="sqliteDbPath">SQLite file path for the qifu token/config cache. Falls back to <c>QIFU_DB_PATH</c>, then <c>~/.aether/qifu.db</c>.</param>
    public static IServiceCollection AddQifuInfrastructure(
        this IServiceCollection services,
        CloudDocsEnv cloudDocsEnv = CloudDocsEnv.Prod,
        string? yushuResultsDir = null,
        string? sqliteDbPath = null)
    {
        // qifu's own config/token cache (SQLite). ICurrentUser + IAppServerHttpClient
        // come from the Platform context.
        var dbPath = sqliteDbPath
            ?? Environment.GetEnvironmentVariable("QIFU_DB_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aether", "qifu.db");
        services.AddSingleton<IQifuConfigCache>(_ => SqliteQifuConfigCache.ForFile(dbPath));

        // CloudDocs (auth via Playwright + upload over sid cookie)
        services.AddSingleton<ICloudDocsAuthService>(sp => new CloudDocsAuthService(
            new HttpClient(),
            sp.GetRequiredService<IQifuConfigCache>(),
            sp.GetRequiredService<ICurrentUser>(),
            cloudDocsEnv,
            sp.GetRequiredService<ILogger<CloudDocsAuthService>>(),
            sp.GetService<ICloudDocsTokenSyncService>()));
        services.AddSingleton<ICloudDocsService>(sp => new CloudDocsService(
            new HttpClient(), sp.GetRequiredService<ICloudDocsAuthService>(), timeoutSeconds: 300));

        // Yushu — 毓数 lowcode OAuth（Teams 消息等仍用）+ mcphub 网关鉴权（SQL 查询/权限工单统一走 mcphub）。
        services.AddSingleton<IYushuAuthService>(sp => new YushuAuthService(
            new HttpClient(),
            sp.GetRequiredService<IQifuConfigCache>(),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<ILogger<YushuAuthService>>(),
            sp.GetService<IYushuTokenSyncService>()));
        services.AddSingleton<IMcphubAuthService>(sp => new McphubAuthService(
            new HttpClient(),
            sp.GetRequiredService<ILogger<McphubAuthService>>(),
            sp.GetService<IMcphubTokenSyncService>()));
        services.AddSingleton<IYushuService>(sp => new YushuService(
            new HttpClient(), sp.GetRequiredService<IMcphubAuthService>(), yushuResultsDir));

        return services;
    }
}
