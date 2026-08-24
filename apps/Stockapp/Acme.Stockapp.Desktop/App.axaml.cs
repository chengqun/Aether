using System;
using System.IO;
using Acme.Ctp.AI;
using Acme.Ctp.Application.DependencyInjection;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.Desktop.Shared.Hosting;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Quotes.AI;
using Acme.Quotes.Application.DependencyInjection;
using Acme.Quotes.Infrastructure.DependencyInjection;
using Acme.Securities.Application.DependencyInjection;
using Acme.Securities.Infrastructure.DependencyInjection;
using Acme.Stockapp.Desktop.ViewModels;
using Acme.Trading.AI;
using Acme.StockTrading.AI;
using Acme.Trading.Application.DependencyInjection;
using Acme.Trading.Runtime.Lean.DependencyInjection;
using Acme.Trading.Scripting;
using Acme.StockTrading.Application;
using Acme.StockTrading.Application.DependencyInjection;
using Acme.StockTrading.Domain;
using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Acme.AppAutomation.McpClientHost;
using Acme.AppAutomation.StockTradingBridge;
using Aether.AI.Abstractions;
using Aether.AI.Hosting;
#if WINDOWS
using Acme.StockTrading.Infrastructure;
using Acme.StockTrading.Infrastructure.DependencyInjection;
#endif
using Microsoft.Extensions.Logging;
using Acme.Stockapp.Desktop.Hosting;
using Acme.Notes.Application;
using Acme.Trading.Runtime.Domain;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop;

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
        DesktopApp.Services = Services;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<StockappMainViewModel>();
            // ★ 窗口 Show 前同步恢复主题，使第一帧即渲染选中主题（否则窗口先按默认 light 渲染，
            // 再被 InitializeAsync 的 FireAndForget 异步切——闪一下默认色，侧边栏最先被看到）。
            vm.RestoreThemeSync();
            desktop.MainWindow = new WorkbenchWindow { DataContext = vm };
            // 退出时释放 DI 容器（含 MCP server 连接等 IAsyncDisposable 单例，关闭 stdio 子进程）。
            // 不阻塞 UI 线程：DisposeAsync 里 MCP McpClient.DisposeAsync 等子进程退出无超时机制（死等），
            // 旧版 .Wait(3s) 在本回调（UI 线程）同步阻塞 → 退出卡顿。改后台线程 + 3s 超时竞速：
            // 超时放弃释放、主进程照常退出（MCP 子进程属本进程树，随进程退出由 OS 回收）。
            desktop.ShutdownRequested += (_, _) =>
            {
                // dock 右键 Quit / Cmd+Q 不经 ExitApplication——先标主窗真退出 + 关桌宠，
                // 否则 OnClosing 见 _isReallyExiting=false 会拦截系统退出 → 退出崩溃（虽最终退出）。
                try { (desktop.MainWindow as WorkbenchWindow)?.PrepareForShutdown(); } catch { /* 忽略 */ }
                try
                {
                    if (Services is System.IAsyncDisposable disposable)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var disposeTask = disposable.DisposeAsync().AsTask();
                                await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
                            }
                            catch { /* 退出期释放失败/超时忽略 */ }
                        });
                    }
                }
                catch { /* 退出期释放失败/超时忽略 */ }
            };
            // 窗口打开后异步初始化登录态（检查已缓存 token，刷新账户显示），不阻塞 UI
            vm.InitializeAsync().SafeFireAndForget();
            // 启动加载策略脚本笔记 → 编译 → StrategyRegistry（NotesChanged 时自动重加载）
            var scripting = Services.GetRequiredService<IStrategyScriptingService>();
            var notes = Services.GetRequiredService<INoteService>();
            notes.NotesChanged += () => scripting.Reload(); // 笔记变更 → 防抖重编译注册策略
            scripting.LoadAsync().SafeFireAndForget();
            // 启动加载 Lean 算法脚本笔记 → Roslyn 编译 → LeanAlgorithmRegistry（NotesChanged 时自动重加载）
            var leanScripting = Services.GetRequiredService<ILeanAlgorithmScriptingService>();
            notes.NotesChanged += () => leanScripting.Reload();
            leanScripting.LoadAsync().SafeFireAndForget();
            // 启动加载代码规则脚本笔记 → Roslyn 编译 → ICodeBrainRegistry（NotesChanged 时自动重加载）
            CodeRuleHost.Start(Services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        // 租户数据目录（绝对路径：.app 双击启动 CWD=/ 不可写）。Mac 用 ~/Library/Application Support（保数据连续），
        // Windows/Linux 用 %LOCALAPPDATA%（系统标准应用数据目录）。
        var tenantDir = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Aether", "Desktop-Stockapp")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aether", "Desktop-Stockapp");

        var services = new ServiceCollection();

        // stockapp 租户专属业务上下文（行情/交易/回测；不含 qifu）
        services.AddCtpInfrastructure(sqliteDbPath: Path.Combine(tenantDir, "ctp.db"));
        services.AddCtpApplication();
        services.AddSecurityInfrastructure(sqliteDbPath: Path.Combine(tenantDir, "securities.db"));
        services.AddSecurityApplication();
        services.AddQuotesInfrastructure(sqliteDbPath: Path.Combine(tenantDir, "quotes.db"));
        services.AddQuotesApplication();
        services.AddTradingApplication();
        services.AddTradingRuntimeLean();
        // 股票交易：两种下单方式经路由 brokerage 运行时切换（FlaUI / MCP），见 RoutingStockBrokerage。
        services.AddStockTradingApplication();
#if WINDOWS
        // FlaUI 后端（Windows-only）。AddStockTradingFlaUi 注册 options + IStockTradingOptions + IStockBrokerage(unkeyed)。
        // 这里额外把 FlaUI 注册为 keyed "flaui"（router 经此拿 FlaUI 实例）；其 unkeyed 注册被下面的 router 覆盖。
        services.AddStockTradingFlaUi();
        services.AddKeyedSingleton<IStockBrokerage>("flaui", (sp, _) =>
            new FlaUiStockBrokerage(sp.GetRequiredService<FlaUiStockBrokerageOptions>(),
                sp.GetService<ILogger<FlaUiStockBrokerage>>()));
#else
        // 非 Windows：FlaUI 后端为「未连接」桩，页面能解析、能打开但操作提示不支持。
        services.AddSingleton<IStockTradingOptions>(new UnsupportedStockTradingOptions());
        services.AddKeyedSingleton<IStockBrokerage>("flaui", new UnsupportedStockBrokerage(
            "股票交易（FlaUI 同花顺）仅支持 Windows。当前操作系统不支持自动下单。"));
#endif
        // MCP 后端（AppAutomation 固定过程路径）。IMcpToolInvoker 复用 IMcpServerRegistry 已连的 desktop server
        // （与专家页 MCP tab 同源——配一份 mcp-servers/desktop.json 两条路径都通）。registry 由 AddDesktopCommon 注册，
        // 在 MainViewModelBase.InitializeAsync 后台 StartAsync 连好；RegistryMcpToolInvoker 按工具名直调 AIFunction。
        // 非 Windows / 未配 desktop server → IsConnected 恒 false，ProcedureRunner 优雅失败「桌面控制后端未连接」。
        services.AddAppAutomation();
        services.AddSingleton<IMcpToolInvoker>(sp => new RegistryMcpToolInvoker(
            sp.GetRequiredService<IMcpServerRegistry>(), sp.GetService<ILoggerFactory>()));
        services.AddKeyedSingleton<IStockBrokerage>("mcp", (sp, _) =>
            new AppAutomationStockBrokerage(sp.GetRequiredService<IProcedureRunner>()));
        // 路由 brokerage = 默认 IStockBrokerage（同时注册具体类型供 VM 注入选方式）。
        services.AddSingleton<RoutingStockBrokerage>();
        services.AddSingleton<IStockBrokerage>(sp => sp.GetRequiredService<RoutingStockBrokerage>());

        // 公共组合（AppAuth/Llm/Notes/Automation/ChatSocial/DeviceConnect/DeviceManagement + 公共 AI 工具）
        var aether = services.AddDesktopCommon("stockapp", tenantDir);
        aether.AddQuotesTools().AddCtpTools().AddTradingTools().AddStockTradingTools();   // stockapp 租户专属 AI 工具
        services.AddTradingScripting(); // 策略脚本加载（需在 AddDesktopCommon 后，依赖 INoteService）
        services.AddSingleton<IStrategyNoteSource, StrategyNoteSource>(); // 桥接 Notes → 策略笔记源
        services.AddSingleton<IStrategyFormCodec, StrategyFormCodec>(); // YAML 策略表单 codec（Stockapp 专属，引 Trading.Scripting）
        // Lean 算法策略：第三种策略写法（与 Lean 直接耦合，独立链路，不动 IStrategy/YAML 路径）。
        // 须在 AddDesktopCommon 后（依赖 INoteService）+ AddTradingRuntimeLean 后（引 Lean 程序集）。
        services.AddLeanAlgorithmServices(tenantDir);
        services.AddSingleton<ILeanAlgorithmNoteSource, LeanAlgorithmNoteSource>(); // 桥接 Notes → Lean 算法笔记源

        // 工作台引擎（view registry / region manager / workspace manager + Stockapp 工作台定义）。
        // 租户专属页（期货/回测/股票）经 contributor 注入 view registry——Workbench 库不依赖租户程序集。
        services.AddWorkbench(StockappWorkspace.Definition);
        services.AddSingleton<IViewRegistryContributor, StockappViewContributor>();

        // 连接器子面板（CTP 期货 + 同花顺股票，并列在连接器 tab）
        services.AddSingleton<IConnectorSettingsDialogService, StockappConnectorSettingsDialogService>();
        services.AddSingleton<CtpConnectorViewModel>();
        services.AddSingleton<ThsConnectorViewModel>();
        // 复合连接器 = CTP + 同花顺，实现 IConnectorViewModel 注入 ExpertsViewModel
        services.AddSingleton<StockappConnectorViewModel>();
        services.AddSingleton<IConnectorViewModel>(sp => sp.GetRequiredService<StockappConnectorViewModel>());
        // 期货页 ViewModel（行情 + 交易）
        services.AddSingleton<CtpViewModel>();
        // 回测/实盘页 ViewModel
        services.AddSingleton<TradingViewModel>();
        // 股票交易页 ViewModel
        services.AddSingleton<StockTradingViewModel>();

        // shell 主 ViewModel
        services.AddSingleton<StockappMainViewModel>();
        services.AddSingleton<MainViewModelBase>(sp => sp.GetRequiredService<StockappMainViewModel>());

        return services.BuildServiceProvider();
    }
}

#if !WINDOWS
/// <summary>非 Windows 平台的占位实现：所有操作返回「不支持」，让页面能解析、能打开但不可用。</summary>
file sealed class UnsupportedStockBrokerage : IStockBrokerage
{
    private readonly string _reason;
    public UnsupportedStockBrokerage(string reason) => _reason = reason;
    public bool IsConnected => false;
    public event EventHandler<string>? LogReceived;
    public Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default)
        => Task.FromResult((false, _reason));
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default)
        => Task.FromResult((false, _reason));
    public Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default)
        => Task.FromResult((false, _reason));
    public Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default)
        => Task.FromResult((false, _reason));
    public Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StockPosition>>(Array.Empty<StockPosition>());
}

/// <summary>非 Windows 平台的空壳配置：字段可读写但不被任何 brokerage 消费（桩 brokerage 忽略）。</summary>
file sealed class UnsupportedStockTradingOptions : IStockTradingOptions
{
    public string ClientExecutablePath { get; set; } = "";
    public string ClientWindowTitle { get; set; } = "网上股票交易系统5.0";
    public int StepDelayMs { get; set; } = 100;
}
#endif
