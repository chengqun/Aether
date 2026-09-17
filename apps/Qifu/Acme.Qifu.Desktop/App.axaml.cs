using System;
using System.IO;
using Acme.DataQuery.AI;
using Acme.DataQuery.Application;
using Acme.DataQuery.Infrastructure;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Hosting;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Shared.Telemetry;
using Acme.Desktop.Workbench;
using Acme.Qifu.AI;
using Acme.Qifu.Application.DependencyInjection;
using Acme.Qifu.Desktop.Hosting;
using Acme.Qifu.Infrastructure.DependencyInjection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Acme.Qifu.Desktop.ViewModels;

namespace Acme.Qifu.Desktop;

public partial class App : global::Avalonia.Application
{
    /// <summary>全局 DI 容器。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 启动期异常（DI 解析/窗口构造/初始化）→ 崩溃遥测 Fatal 记录后原样上抛（不吞不改行为）。
        try
        {
            Services = ConfigureServices();
            DesktopApp.Services = Services;
            // 崩溃遥测：启动上报器（crash.db 队列 → 登录后 drain 到服务端）。在 Startup 包裹内——
            // 其内部失败只记非致命；AETHER_FORCE_CRASH 验证钩子的 throw 逃到外层 catch 记 Fatal 后上抛。
            CrashTelemetryHost.Start(Services);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var vm = Services.GetRequiredService<QifuMainViewModel>();
                // ★ 窗口 Show 前同步恢复主题，使第一帧即渲染选中主题（否则先按默认 light 渲染再异步切，闪一下）。
                vm.RestoreThemeSync();
                desktop.MainWindow = new WorkbenchWindow { DataContext = vm };
                // 退出时释放 DI 容器（含 MCP server 连接等 IAsyncDisposable 单例，关闭 stdio 子进程）。
                // 不阻塞 UI 线程：DisposeAsync 里 MCP McpClient.DisposeAsync 等子进程退出无超时机制（死等），
                // 旧版 .Wait(3s) 在本回调（UI 线程）同步阻塞 → 退出卡顿。改后台线程 + 3s 超时竞速：
                // 超时放弃释放、主进程照常退出（MCP 子进程属本进程树，随进程退出由 OS 回收）。
                desktop.ShutdownRequested += async (_, _) =>
                {
                    // dock 右键 Quit / Cmd+Q 不经 ExitApplication——先标主窗真退出 + 关桌宠，
                    // 否则 OnClosing 见 _isReallyExiting=false 会拦截系统退出 → 退出崩溃（虽最终退出）。
                    try { (desktop.MainWindow as WorkbenchWindow)?.PrepareForShutdown(); }
                    catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
                    // 容器释放改「同步 await + 总超时上限」：旧版 fire-and-forget Task.Run 与主进程退出赛跑——
                    // 后台释放线程（McpClient.DisposeAsync 关 stdio 子进程，无上限阻塞）在进程 tear-down 后才跑完，
                    // 子进程在父进程死后才收到断开 → 报错/崩。此处 await 把它在进程收尾前接住，
                    // 释放完整跑完（含子进程干净退出）后 ShutdownRequested 才返回、进程才退出，消除赛跑。
                    try
                    {
                        if (Services is System.IAsyncDisposable disposable)
                        {
                            try
                            {
                                await disposable.DisposeAsync()
                                    .AsTask()
                                    .WaitAsync(TimeSpan.FromSeconds(3));
                            }
                            catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
                        }
                    }
                    catch (Exception ex) { CrashRecorder.Record(CrashSource.Shutdown, CrashSeverity.NonFatal, ex); }
                };
                // 窗口打开后异步初始化登录态 + 业务 VM + Chat 引擎 + 激活默认工作台，不阻塞 UI。
                vm.InitializeAsync().SafeFireAndForget();
                // 启动加载代码规则脚本文件 → Roslyn 编译 → ICodeBrainRegistry（labs 目录变更时自动重加载）
                CodeRuleHost.Start(Services);
                // 启动 labs 目录 watcher（驱动代码规则的 labs 文件变更 → 重编译）
                Services.GetRequiredService<LabsDirectoryWatcher>().Start();
            }
        }
        catch (Exception ex)
        {
            CrashRecorder.Record(CrashSource.Startup, CrashSeverity.Fatal, ex);
            throw;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        // 租户数据目录（绝对路径：.app 双击启动 CWD=/ 不可写）。单一来源 TenantDataPaths——
        // Program.cs 的崩溃遥测初始化（DI 之前）也要用同一目录。两租户各自独立。
        var tenantDir = TenantDataPaths.GetTenantDir("Qifu");

        var services = new ServiceCollection();

        // qifu 租户专属业务上下文（毓数/云文档/Teams）
        // yushuResultsDir 落 <tenantDir>/.yushu/results（默认 ~/.yushu/results 会污染用户主目录且不按租户隔离）
        services.AddQifuInfrastructure(yushuResultsDir: Path.Combine(tenantDir, ".yushu", "results"),
                                       sqliteDbPath: Path.Combine(tenantDir, "qifu.db"));
        services.AddQifuApplication();

        // 公共组合（AppAuth/Llm/Notes/Automation/ChatSocial/DeviceConnect/DeviceManagement + 公共 AI 工具）
        var aether = services.AddDesktopCommon("qifu", tenantDir);
        aether.AddQifuTools();            // 双端通用工具（毓数 SQL / 权限工单 / Teams）
        aether.AddQifuCloudDocsTools();   // 桌面-only 工具（云文档浏览器登录 / 上传）

        // MySQL 直连数据查询（Infrastructure 含 MySqlConnector + AI 工具 ExecuteSql / Dangerous）
        services.AddDataQueryInfrastructure();
        services.AddDataQueryApplication();
        aether.AddDataQueryTools();

        // 工作台引擎（view registry / region manager / workspace manager + Qifu 工作台定义）。
        services.AddWorkbench(QifuWorkspace.Definition);

        // MySQL 连接器卡的设置窗服务（Qifu 首张设置对话框卡；毓数/mcphub/云文档直接登录不经此服务）
        services.AddSingleton<IConnectorSettingsDialogService, QifuConnectorSettingsDialogService>();

        // Qifu 连接器（毓数 + mcphub + 云文档 + MySQL；注入 ExpertsViewModel 的 Connector tab）
        services.AddSingleton<IConnectorViewModel, QifuConnectorViewModel>();

        // shell 主 ViewModel（复用 MainViewModelBase 账号/业务 VM/Chat + 工作台层）。
        services.AddSingleton<QifuMainViewModel>();
        services.AddSingleton<MainViewModelBase>(sp => sp.GetRequiredService<QifuMainViewModel>());

        return services.BuildServiceProvider();
    }
}
