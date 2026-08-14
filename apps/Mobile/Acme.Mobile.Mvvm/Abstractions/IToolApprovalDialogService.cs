using Aether.AI.Core.Agents;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 工具审批对话框服务（手机端 MAUI 实现见 Acme.Mobile.Shared）。
/// agent 触发需审批工具（RequireToolApproval）时，流式以 <see cref="ToolApprovalRequest"/> 上抛；
/// 本服务弹 ActionSheet（批准/拒绝/总是批准/取消）阻塞等待用户决定，返回 <see cref="ToolApprovalDecision"/>。
/// 取消/Dismiss 返回 null（调用方据此终止本轮）。镜像桌面 <c>IApprovalDialogService</c>。
/// </summary>
public interface IToolApprovalDialogService
{
    Task<ToolApprovalDecision?> ShowApprovalAsync(ToolApprovalRequest request);
}
