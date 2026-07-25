using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Shared.Shell;

namespace Acme.Qifu.Desktop.ViewModels;

/// <summary>
/// qifu 租户 shell 主 ViewModel：导航栏 4 项（新聊天/专家/自动化/笔记）。
/// 无 CTP/回测（内部版不需要股票期货行情与交易）。
/// </summary>
public sealed class QifuMainViewModel : MainViewModelBase
{
    public QifuMainViewModel(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog)
        : base(auth, currentUser, loginDialog)
    {
        AddNav("chat", "✨", "新聊天");
        AddNav("experts", "🧪", "专家");
        AddNav("automation", "⏰", "自动化");
        AddNav("notes", "📝", "笔记");

        NavigateToCommand.Execute("chat");
    }
}
