namespace Acme.Mobile.Mvvm.Events;

/// <summary>
/// 通讯录变更事件：新增/接受/删除关系时发布，通知通讯录页面刷新
/// </summary>
public class ContactChangedEvent
{
    /// <summary>
    /// "added" | "accepted" | "deleted"
    /// </summary>
    public string Action { get; }

    public ContactChangedEvent(string action)
    {
        Action = action;
    }
}