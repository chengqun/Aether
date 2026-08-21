using System;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Workbench;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Workspace;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// Stockapp 租户 shell 主 ViewModel：复用 MainViewModelBase（账号/业务 VM/Chat/调度器/MCP）+ 工作台层。
/// nav 6 项（笔记/专家/自动化/期货/回测/股票）数据驱动自 <see cref="StockappWorkspace"/> 的 LayoutTabbed——
/// 比 Qifu 多 CTP 期货 + 回测 + 股票。租户专属页 VM（Futures/Trading/StockTrade）由本类自持（类型化），
/// 经 <see cref="StockappViewContributor"/> 注入工作台 view registry——不污染共享基类。
/// </summary>
public sealed class StockappMainViewModel : WorkbenchShellViewModel
{
    // ★ 启动性能：三个租户专属页 VM 惰性解析（Lazy<T>），而非 ctor 注入实例。
    // TradingViewModel 的依赖链（Lean 引擎 + Roslyn 策略编译）冷启动 ~624ms、CtpViewModel ~106ms——
    // 若作 ctor 参数同步构造，会阻塞首帧（窗口 Show 前转圈）。改为 Lazy 后，首帧不解析 Lean/Ctp，
    // 只在切到对应 tab（contributor vmFactory 调 .Trading/.Futures）时才构建。该构建点已处异步路径，
    // 不阻塞首帧。它们都是 singleton，Lazy.Value 解析一次后缓存。
    private readonly Lazy<CtpViewModel> _futures;
    private readonly Lazy<TradingViewModel> _trading;
    private readonly Lazy<StockTradingViewModel> _stockTrade;

    /// <summary>期货页 VM（contributor 注册 FuturesView，DataContext 绑此）。类型化，不暴露为 object。</summary>
    public CtpViewModel Futures => _futures.Value;

    /// <summary>回测/实盘页 VM（TradingView）。</summary>
    public TradingViewModel Trading => _trading.Value;

    /// <summary>股票交易页 VM（StockTradingView，FlaUI/MCP 路由）。</summary>
    public StockTradingViewModel StockTrade => _stockTrade.Value;

    public StockappMainViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore,
        ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient,
        ILlmConfigService llmService,
        IWorkspaceManager workspaceManager, WorkspaceDefinition defaultWorkspace)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService,
               workspaceManager, defaultWorkspace, "stockapp")
    {
        _futures = new Lazy<CtpViewModel>(() => serviceProvider.GetRequiredService<CtpViewModel>());
        _trading = new Lazy<TradingViewModel>(() => serviceProvider.GetRequiredService<TradingViewModel>());
        _stockTrade = new Lazy<StockTradingViewModel>(() => serviceProvider.GetRequiredService<StockTradingViewModel>());
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync(); // 账号/业务 VM/Chat/调度器/MCP + 激活默认工作台
        // 期货页启动初始化（刷新连接器配置/加载主力合约/订阅；含服务器证券同步）。此处在异步路径触发
        // _futures.Value（首帧已过，构造 CtpViewModel 不再阻塞窗口 Show）。
        await _futures.Value.InitializeAsync();
    }
}
