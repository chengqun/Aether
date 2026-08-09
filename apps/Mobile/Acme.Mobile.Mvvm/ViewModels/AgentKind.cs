namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 手机端聊天会话的 agent 种类（UI 分流用）。对应 <see cref="Acme.ChatSocial.Domain.Conversation.Kind"/>
/// 的 int? 值：0=Agent(LLM)、1=Workflow、2=RuleBased。移植自旧 AlphaAgent 的 AgentKind 枚举（值不变）。
/// </summary>
public enum AgentKind
{
    /// <summary>LLM 驱动的智能体。</summary>
    Agent = 0,

    /// <summary>多步骤工作流。</summary>
    Workflow = 1,

    /// <summary>规则驱动、无 LLM。</summary>
    RuleBased = 2,
}
