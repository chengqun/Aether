using Acme.Mobile.Mvvm.Services;
using Acme.Qifu.Application.DependencyInjection;
using Acme.Qifu.Domain.Ports;
using Acme.Qifu.Domain.Yushu;
using Acme.Qifu.Http.Yushu;
using Acme.Qifu.Mobile.Persistence;
using Acme.Qifu.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
// Domain 与 Mvvm 都有 IMcphubAuthService（同名接口）；本文件同时引用两者，故给 Domain 层加别名。
using DomainMcphubAuth = Acme.Qifu.Domain.Yushu.IMcphubAuthService;
using MvvmMcphubAuth = Acme.Mobile.Mvvm.Services.IMcphubAuthService;

namespace Acme.Qifu.Mobile.Hosting;

/// <summary>
/// 毓数/云文档 移动端服务注册。
///
/// 不引 <c>Acme.Qifu.Infrastructure</c>（含 <c>Microsoft.Playwright</c>，net10.0-ios 不支持）——
/// 手机端 <see cref="IQifuConfigCache"/> 在本项目内重实现（<see cref="MobileQifuConfigCache"/>，SqliteStoreBase），
/// 业务 token 同步服务走干净的 <c>Acme.Qifu.Application</c>（<see cref="QifuApplicationExtensions.AddQifuApplication"/>），
/// 认证/同步端口由本项目 4 个适配器桥接到 <see cref="Acme.Mobile.Mvvm.Services"/> 下的手机 VM 层本地端口。
///
/// 注册后毓数/云文档/mcphub 三张 auth 卡在「连接器」页显示（ConnectorViewModel.Cards），
/// "从服务器同步"按钮拉桌面端已上传的 token/sid/JWT（依赖桌面端先登录过）。
/// </summary>
internal static class QifuMobileExtensions
{
    public static IServiceCollection AddQifuMobileServices(this IServiceCollection services, string tenantDir)
    {
        var qifuDbPath = Path.Combine(tenantDir, "qifu.db");

        // 1. 手机端 IQifuConfigCache（SQLite，单用户）——业务同步服务依赖此端口读写 token/sid
        services.AddSingleton<IQifuConfigCache>(_ => MobileQifuConfigCache.ForFile(qifuDbPath));

        // 2. 业务 token 同步服务（YushuTokenSyncService/CloudDocsTokenSyncService，干净——无 Playwright）
        //    依赖 IAppServerHttpClient + IQifuConfigCache + ICurrentUser（均由 AddMobileCommon→AddAppAuthInfrastructure 注册）
        services.AddQifuApplication();

        // 3. 手机 VM 层本地端口适配器（实现 Acme.Mobile.Mvvm.Services.* 接口，桥接到业务同步服务）
        services.AddSingleton<Acme.Mobile.Mvvm.Services.IYushuAuthService, MobileYushuAuthService>();
        services.AddSingleton<Acme.Mobile.Mvvm.Services.IYushuTokenSyncService, MobileYushuTokenSyncService>();
        services.AddSingleton<Acme.Mobile.Mvvm.Services.ICloudDocsAuthService, MobileCloudDocsAuthService>();
        services.AddSingleton<Acme.Mobile.Mvvm.Services.ICloudDocsTokenSyncService, MobileCloudDocsTokenSyncService>();

        // 4. Domain 层 Yushu 业务能力（供 AddQifuTools 的毓数 SQL 工具使用）：
        //    mcphub JWT 鉴权（手机版=从服务器拉取）+ YushuService（纯 HTTP，来自 Acme.Qifu.Http，无 Playwright）。
        //    云文档浏览器类工具（LoginCloudDocs/UploadCloudDocsFolder/File）手机端无浏览器，不注册 ICloudDocsService
        //    → 对应工具在 agent 调用时 DI 解析失败并报错（可接受：手机端业务边界不含浏览器登录）。
        services.AddSingleton<DomainMcphubAuth, MobileMcphubAuthService>();
        // 同一单例同时暴露给 AI 设置页 mcphub 卡（Mvvm 层 UI 端口）——Mvvm 层不能引 Domain，故经这里别名绑定。
        services.AddSingleton<MvvmMcphubAuth>(
            sp => (MvvmMcphubAuth)sp.GetRequiredService<DomainMcphubAuth>());
        services.AddSingleton<IYushuService>(sp => new YushuService(
            new HttpClient(),
            sp.GetRequiredService<DomainMcphubAuth>(),
            Path.Combine(tenantDir, ".yushu", "results")));

        return services;
    }
}
