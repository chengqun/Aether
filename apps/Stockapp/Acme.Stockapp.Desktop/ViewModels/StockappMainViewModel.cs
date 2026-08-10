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
    private readonly CtpViewModel _ctpVm;

    /// <summary>期货页 VM（contributor 注册 FuturesView，DataContext 绑此）。类型化，不暴露为 object。</summary>
    public CtpViewModel Futures { get; }

    /// <summary>回测/实盘页 VM（TradingView）。</summary>
    public TradingViewModel Trading { get; }

    /// <summary>股票交易页 VM（StockTradingView，FlaUI/MCP 路由）。</summary>
    public StockTradingViewModel StockTrade { get; }

    public StockappMainViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore,
        ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient,
        ILlmConfigService llmService, CtpViewModel ctpVm, TradingViewModel tradingVm, StockTradingViewModel stockTradingVm,
        IWorkspaceManager workspaceManager, WorkspaceDefinition defaultWorkspace)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService,
               workspaceManager, defaultWorkspace, "stockapp")
    {
        _ctpVm = ctpVm;
        Futures = ctpVm;
        Trading = tradingVm;
        StockTrade = stockTradingVm;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync(); // 账号/业务 VM/Chat/调度器/MCP + 激活默认工作台
        await _ctpVm.InitializeAsync();
    }
}
