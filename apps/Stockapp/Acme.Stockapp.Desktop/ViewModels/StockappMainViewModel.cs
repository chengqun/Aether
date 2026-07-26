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
    public StockappMainViewModel(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService, "stockapp")
    {
        AddNav("chat", "✨", "新聊天");
        AddNav("experts", "🧪", "专家");
        AddNav("automation", "⏰", "自动化");
        AddNav("notes", "📝", "笔记");
        AddNav("ctp", "📈", "期货");
        AddNav("backtest", "📊", "回测");

        NavigateToCommand.Execute("chat");
    }
}
