using Aether.AI.Core.Agents;

namespace Acme.Shell;

/// <summary>
/// 弹出工具审批对话框，返回用户的审批决定；用户关闭/取消返回 <c>null</c>。
/// UI 无关接口；实现由各 UI 框架提供。
/// </summary>
public interface IApprovalDialogService
{
    Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request);
}
