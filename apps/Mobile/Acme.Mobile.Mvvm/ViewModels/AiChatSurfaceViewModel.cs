using Acme.Agents.AI;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;
using Aether.AI.Hosting;
using Aether.AI.Memory;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// AI tab 常驻聊天面板 VM——<see cref="AgentChatDetailViewModel"/> 的子类，仅用于 DI Singleton 注册。
/// AI tab 持有此 Singleton 实例常驻；抽屉切换会话时调 <see cref="AgentChatDetailViewModel.SetParameters"/> 就地重载。
/// 推送页 <c>AgentChatDetailPage</c> 仍用 Transient 基类实例（互不干扰；基类不订阅 EventBus 事件，无双订）。
/// </summary>
/// <remarks><b>必须显式声明构造函数转发基类依赖</b>：基类 ctor 全为可选参，空子类的隐式无参 ctor 会令
/// <c>_agentRunner</c>/<c>_conversationStore</c>/<c>_uiThreadDispatcher</c> 等全部为 null（DI 不会自动注入
/// 到基类）→ 发送静默失败、UI 更新被 null dispatcher 吞掉。此处显式转发让 DI 注入。</remarks>
public class AiChatSurfaceViewModel : AgentChatDetailViewModel
{
    public AiChatSurfaceViewModel(
        ICustomAgentRunner? agentRunner = null,
        IConversationStore? conversationStore = null,
        ICustomAgentCatalog? agentCatalog = null,
        IEventBusService? eventBusService = null,
        IUIThreadDispatcher? uiThreadDispatcher = null,
        IClipboardService? clipboardService = null,
        IToastService? toastService = null,
        ISpeechRecognitionService? speechRecognitionService = null,
        IStreamingBackgroundService? streamingBackgroundService = null,
        IConversationTitleService? titleService = null,
        IToolApprovalDialogService? approvalDialog = null)
        : base(agentRunner, conversationStore, agentCatalog, eventBusService, uiThreadDispatcher,
               clipboardService, toastService, speechRecognitionService, streamingBackgroundService, titleService,
               approvalDialog)
    {
    }
}
