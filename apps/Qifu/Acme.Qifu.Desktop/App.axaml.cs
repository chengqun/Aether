using System;
using System.IO;
using Acme.Desktop.Shared.Hosting;
using Acme.Shell;
using Acme.Desktop.Shared.Shell;
using Acme.Qifu.AI;
using Acme.Qifu.Application.DependencyInjection;
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
        Services = ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<QifuMainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // 窗口打开后异步初始化登录态（检查已缓存 token，刷新账户显示），不阻塞 UI
            _ = vm.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        // 租户数据目录（绝对路径：.app 双击启动 CWD=/ 不可写）。两租户各自独立。
        var tenantDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Aether", "Desktop-Qifu");

        var services = new ServiceCollection();

        // qifu 租户专属业务上下文（毓数/云文档/Teams）
        services.AddQifuInfrastructure(sqliteDbPath: Path.Combine(tenantDir, "qifu.db"));
        services.AddQifuApplication();

        // 公共组合（AppAuth/Llm/Notes/Automation/ChatSocial/DeviceConnect/DeviceManagement + 公共 AI 工具）
        var aether = services.AddDesktopCommon("qifu", tenantDir);
        aether.AddQifuTools();   // qifu 租户专属 AI 工具（毓数 SQL/云文档/Teams）

        // shell 主 ViewModel
        services.AddSingleton<QifuMainViewModel>();
        services.AddSingleton<MainViewModelBase>(sp => sp.GetRequiredService<QifuMainViewModel>());

        return services.BuildServiceProvider();
    }
}
