using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Workbench;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Workspace;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// Qifu 租户 shell 主 ViewModel：复用 MainViewModelBase（账号/业务 VM/Chat/调度器/MCP）+ 工作台层。
/// nav 3 项（笔记/专家/自动化）数据驱动自 <see cref="QifuWorkspace"/> 的 LayoutTabbed——无 CTP/回测/股票（内部版不需要行情与交易）。
/// </summary>
public sealed class QifuMainViewModel : WorkbenchShellViewModel
{
    public QifuMainViewModel(
        IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore,
        ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient,
        ILlmConfigService llmService,
        IWorkspaceManager workspaceManager, WorkspaceDefinition defaultWorkspace)
        : base(auth, currentUser, loginDialog, tokenStore, settingsDialog, serviceProvider, deviceChatClient, llmService,
               workspaceManager, defaultWorkspace, "qifu")
    {
    }
}
