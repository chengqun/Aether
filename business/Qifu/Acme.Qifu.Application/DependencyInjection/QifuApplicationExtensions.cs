using Acme.AppAuth.Domain;
using Acme.Qifu.Application.CloudDocs;
using Acme.Qifu.Application.Yushu;
using Acme.Qifu.Domain.CloudDocs;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.Yushu;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.Application.DependencyInjection;

/// <summary>
/// DI entry point for the qifu Application services — the cross-device token
/// sync services. They depend on the Platform ports (<see cref="IAppServerHttpClient"/>,
/// <see cref="ICurrentUser"/>) and qifu's <see cref="IQifuConfigCache"/>, all
/// registered by <c>AddAppAuthInfrastructure</c> + <c>AddQifuInfrastructure</c>.
/// </summary>
public static class QifuApplicationExtensions
{
    public static IServiceCollection AddQifuApplication(this IServiceCollection services)
    {
        services.AddSingleton<ICloudDocsTokenSyncService>(sp => new CloudDocsTokenSyncService(
            sp.GetRequiredService<IAppServerHttpClient>(),
            sp.GetRequiredService<IQifuConfigCache>(),
            sp.GetRequiredService<ICurrentUser>()));
        services.AddSingleton<IYushuTokenSyncService>(sp => new YushuTokenSyncService(
            sp.GetRequiredService<IAppServerHttpClient>(),
            sp.GetRequiredService<IQifuConfigCache>(),
            sp.GetRequiredService<ICurrentUser>()));
        services.AddSingleton<IMcphubTokenSyncService>(sp => new McphubTokenSyncService(
            sp.GetRequiredService<IAppServerHttpClient>(),
            sp.GetRequiredService<ICurrentUser>()));
        return services;
    }
}
