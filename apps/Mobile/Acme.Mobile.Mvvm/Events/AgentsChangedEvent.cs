namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// Agent 变更事件：召唤/删除/编辑本地 Agent 后发布，通知通讯录 AI 助手分组刷新。
/// 区别于 <see cref="ContactChangedEvent"/>（关系链变更，触发全量通讯录同步）——本事件只刷新 AI 助手分组。
/// </summary>
public class AgentsChangedEvent
{
    /// <summary>"summoned" | "deleted" | "edited"</summary>
    public string Action { get; }

    public AgentsChangedEvent(string action = "summoned")
    {
        Action = action;
    }
}
