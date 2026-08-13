namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// AI tab 常驻聊天面板 VM——<see cref="AgentChatDetailViewModel"/> 的空壳子类，仅用于 DI Singleton 注册。
/// AI tab 持有此 Singleton 实例常驻；抽屉切换会话时调 <see cref="AgentChatDetailViewModel.SetParameters"/> 就地重载。
/// 推送页 <c>AgentChatDetailPage</c> 仍用 Transient 基类实例（互不干扰；基类不订阅 EventBus 事件，无双订）。
/// </summary>
public class AiChatSurfaceViewModel : AgentChatDetailViewModel
{
}
