namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// LLM 配置变更事件：设置 tab 保存/同步 LLM 配置后发布，通知 AI 聊天下拉框重载列表。
/// 镜像 <see cref="AgentsChangedEvent"/> 的发布-订阅模式，避免设置 VM 反向引用聊天 VM。
/// </summary>
public class LlmConfigsChangedEvent
{
    /// <summary>"saved" | "synced"</summary>
    public string Action { get; }

    public LlmConfigsChangedEvent(string action = "saved")
    {
        Action = action;
    }
}
