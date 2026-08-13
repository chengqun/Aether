namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// Agent 选择页选定 agent 后发布（经 <c>IEventBusService</c>）。
/// AI tab 的 <c>AiChatViewModel</c> 订阅 → 在内嵌聊天面板内开新对话（<c>SetParameters({agentName})</c>），
/// 而非推送 <c>AgentChatDetailPage</c>。picker 页发布后自行 pop 回 AI tab。
/// </summary>
public class AgentPickedEvent
{
    public string AgentName { get; }

    public AgentPickedEvent(string agentName)
    {
        AgentName = agentName;
    }
}
