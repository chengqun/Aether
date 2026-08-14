using Acme.Mobile.Mvvm.Abstractions;
using Aether.AI.Core.Agents;

namespace Acme.Mobile.Shared.Services;

/// <summary>
/// <see cref="IToolApprovalDialogService"/> 的 MAUI 实现：用 <c>Page.DisplayActionSheet</c> 弹审批选项。
/// 调用方在 agent 后台线程，<see cref="MainThread"/> marshal 到 UI 线程弹框。
/// 选项：批准 / 拒绝 / 总是批准 / 取消。取消或 Dismiss（返回 null）→ 整体返回 null（终止本轮）。
/// </summary>
public sealed class MobileToolApprovalDialogService : IToolApprovalDialogService
{
    public async Task<ToolApprovalDecision?> ShowApprovalAsync(ToolApprovalRequest request)
    {
        var page = Application.Current?.MainPage;
        if (page is null) return null;

        var choice = await MainThread.InvokeOnMainThreadAsync(async () =>
            await page.DisplayActionSheetAsync(
                $"工具「{request.ToolName}」需要审批",
                "取消",
                null,
                "批准", "拒绝", "总是批准")).ConfigureAwait(false);

        return choice switch
        {
            "批准" => new ToolApprovalDecision(request.RequestId, Approved: true, Reason: null, AlwaysApprove: false),
            "拒绝" => new ToolApprovalDecision(request.RequestId, Approved: false, Reason: "用户拒绝", AlwaysApprove: false),
            "总是批准" => new ToolApprovalDecision(request.RequestId, Approved: true, Reason: null, AlwaysApprove: true),
            _ => null, // 取消 / Dismiss → null
        };
    }
}
