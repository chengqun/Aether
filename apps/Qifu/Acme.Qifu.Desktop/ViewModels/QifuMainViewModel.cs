using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Shell;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// qifu 租户 shell 主 ViewModel：导航栏 4 项（新聊天/专家/自动化/笔记）。
/// 无 CTP/回测（内部版不需要股票期货行情与交易）。
/// </summary>
public sealed class QifuMainViewModel : MainViewModelBase
{
    public QifuMainViewModel(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService, "qifu")
    {
        AddNav("chat", NavIcons.Chat, "新聊天");
        AddNav("experts", NavIcons.Experts, "专家");
        AddNav("automation", NavIcons.Automation, "自动化");
        AddNav("notes", NavIcons.Notes, "笔记");

        NavigateToCommand.Execute("chat");
    }
}
