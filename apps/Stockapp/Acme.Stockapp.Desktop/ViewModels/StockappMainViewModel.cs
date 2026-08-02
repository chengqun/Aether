using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Shell;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// stockapp 租户 shell 主 ViewModel：导航栏 6 项（新聊天/专家/自动化/笔记/期货/回测）。
/// 比 qifu 多 CTP 期货 + 回测（外部版股票期货场景）。
/// </summary>
public sealed class StockappMainViewModel : MainViewModelBase
{
    private readonly CtpViewModel _ctpVm;

    public StockappMainViewModel(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService, CtpViewModel ctpVm, TradingViewModel tradingVm)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService, "stockapp")
    {
        _ctpVm = ctpVm;
        Futures = ctpVm;   // 期货页 VM（共享 shell 经 ContentControl + App.axaml DataTemplate 解析为 FuturesView）
        Trading = tradingVm;   // 回测/实盘页 VM（DataTemplate 解析为 TradingView）

        AddNav("notes", NavIcons.Notes, "笔记");
        AddNav("experts", NavIcons.Experts, "专家");
        AddNav("automation", NavIcons.Automation, "自动化");
        AddNav("ctp", NavIcons.Futures, "期货");
        AddNav("backtest", NavIcons.Backtest, "回测");
        NavigateToCommand.Execute("chat");
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await _ctpVm.InitializeAsync();
    }
}
