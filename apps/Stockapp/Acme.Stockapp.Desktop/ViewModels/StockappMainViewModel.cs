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
/// 比 Qifu 多 CTP 期货 + 回测 + 股票。租户页视图经 <see cref="StockappViewContributor"/> 注册。
/// </summary>
public sealed class StockappMainViewModel : WorkbenchShellViewModel
{
    private readonly CtpViewModel _ctpVm;

    public StockappMainViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore,
        ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient,
        ILlmConfigService llmService, CtpViewModel ctpVm, TradingViewModel tradingVm, StockTradingViewModel stockTradingVm,
        IWorkspaceManager workspaceManager, IRegionManager regionManager, WorkspaceDefinition defaultWorkspace)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService,
               workspaceManager, regionManager, defaultWorkspace, "stockapp")
    {
        _ctpVm = ctpVm;
        Futures = ctpVm;          // 期货页 VM（contributor 注册 FuturesView，DataContext 绑此）
        Trading = tradingVm;      // 回测/实盘页 VM（TradingView）
        StockTrade = stockTradingVm; // 股票交易页 VM（StockTradingView，FlaUI/MCP 路由）
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync(); // 账号/业务 VM/Chat/调度器/MCP + 激活默认工作台
        await _ctpVm.InitializeAsync();
    }
}
