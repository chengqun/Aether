using System;
using System.IO;
using Acme.Ctp.AI;
using Acme.Ctp.Application.DependencyInjection;
using Acme.Ctp.Infrastructure.DependencyInjection;
using Acme.Desktop.Shared.Hosting;
using Acme.Shell;
using Acme.Desktop.Shared.Shell;
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
using Acme.StockTrading.Application.DependencyInjection;
using Acme.StockTrading.Domain;
#if WINDOWS
using Acme.StockTrading.Infrastructure.DependencyInjection;
#endif
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
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // 退出时释放 DI 容器（含 MCP server 连接等 IAsyncDisposable 单例，关闭 stdio 子进程）。
            desktop.ShutdownRequested += (_, _) =>
            {
                try { (Services as System.IAsyncDisposable)?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* 退出期释放失败忽略 */ }
            };
            // 窗口打开后异步初始化登录态（检查已缓存 token，刷新账户显示），不阻塞 UI
            _ = vm.InitializeAsync();
            // 启动加载策略脚本笔记 → 编译 → StrategyRegistry（NotesChanged 时自动重加载）
            var scripting = Services.GetRequiredService<IStrategyScriptingService>();
            var notes = Services.GetRequiredService<INoteService>();
            notes.NotesChanged += () => scripting.Reload(); // 笔记变更 → 防抖重编译注册策略
            _ = scripting.LoadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IServiceProvider ConfigureServices()
    {
        var tenantDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Aether", "Desktop-Stockapp");

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
        // 股票交易（FlaUI 操控同花顺下单客户端，Windows-only）
        services.AddStockTradingApplication();
#if WINDOWS
        services.AddStockTradingFlaUi();
#else
        // 非 Windows：注册一个「未连接」桩实现，让页面能解析、能打开，但操作提示不支持。
        services.AddSingleton<IStockBrokerage>(new UnsupportedStockBrokerage(
            "股票交易（FlaUI 同花顺）仅支持 Windows。当前操作系统不支持自动下单。"));
#endif

        // 公共组合（AppAuth/Llm/Notes/Automation/ChatSocial/DeviceConnect/DeviceManagement + 公共 AI 工具）
        var aether = services.AddDesktopCommon("stockapp", tenantDir);
        aether.AddQuotesTools().AddCtpTools().AddTradingTools().AddStockTradingTools();   // stockapp 租户专属 AI 工具
        services.AddTradingScripting(); // 策略脚本加载（需在 AddDesktopCommon 后，依赖 INoteService）
        services.AddSingleton<IStrategyNoteSource, StrategyNoteSource>(); // 桥接 Notes → 策略笔记源

        // CTP 连接器（Stockapp 专属；注入 ExpertsViewModel 的 Connector tab）
        services.AddSingleton<IConnectorViewModel, CtpConnectorViewModel>();
        services.AddSingleton<CtpConnectorViewModel>(sp => (CtpConnectorViewModel)sp.GetRequiredService<IConnectorViewModel>());
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
#endif
