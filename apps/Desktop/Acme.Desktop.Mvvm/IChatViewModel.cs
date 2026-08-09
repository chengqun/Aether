using System.Collections.ObjectModel;

namespace Acme.Desktop.Mvvm;

/// <summary>聊天视图所需的最小契约（供 ChatView 等 UI 绑定）。</summary>
public interface IChatViewModel
{
    ObservableCollection<ChatTurnItem> Messages { get; }
}
