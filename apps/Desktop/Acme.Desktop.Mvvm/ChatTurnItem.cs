using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Shell;

/// <summary>一轮对话的分组容器：用户请求 + 其下所有 agent 产出（思考/工具/答案/审批）。</summary>
public enum TurnStatus
{
    Working,
    Done,
    Error,
    AwaitingApproval,
}

/// <summary>
/// 聊天一轮的视觉分组：左侧竖线 + Children（用户气泡 + agent 卡片）+ 状态/耗时。
/// 纯 UI 聚合，不持久化（持久化仍按 Message 行存，恢复时按 user 消息分组重建）。
/// </summary>
public partial class ChatTurnItem : ObservableObject
{
    /// <summary>用户请求文本（turn 头部展示）。</summary>
    public string Header { get; init; } = string.Empty;

    /// <summary>本轮所有消息项（用户气泡为第一项，其余为 agent 产出）。</summary>
    public ObservableCollection<ChatMessageItem> Children { get; } = new();

    private DateTime _startedAt = DateTime.Now;

    [ObservableProperty]
    private TurnStatus _status = TurnStatus.Working;

    [ObservableProperty]
    private string? _elapsedText;

    public bool IsWorking => Status == TurnStatus.Working;
    public bool IsDone => Status == TurnStatus.Done;
    public bool IsError => Status == TurnStatus.Error;
    public bool IsAwaitingApproval => Status == TurnStatus.AwaitingApproval;

    /// <summary>结束本轮：设状态 + 计算耗时。</summary>
    public void Finish(TurnStatus status)
    {
        Status = status;
        ElapsedText = $"{(DateTime.Now - _startedAt).TotalSeconds:F1}s";
    }

    partial void OnStatusChanged(TurnStatus value)
    {
        OnPropertyChanged(nameof(IsWorking));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsAwaitingApproval));
    }
}
