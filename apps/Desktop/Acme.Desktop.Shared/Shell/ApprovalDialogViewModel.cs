using System.Text.Json;
using System.Windows.Input;
using Aether.AI.Core.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>工具审批对话框 ViewModel：展示工具名+输入，返回批准/拒绝决定。</summary>
public partial class ApprovalDialogViewModel : ObservableObject
{
    private readonly ToolApprovalRequest _request;

    public string ToolName => _request.ToolName;
    public string InputText { get; }

    /// <summary>用户决定；null=取消。命令触发后由 Service 读取并关窗。</summary>
    public ToolApprovalDecision? Result { get; private set; }

    /// <summary>本会话对该工具免问（映射 AlwaysApprove）。</summary>
    [ObservableProperty] private bool _alwaysApprove;

    /// <summary>命令执行完毕触发，Service 订阅以关闭窗口。</summary>
    public event Action? Finished;

    public ApprovalDialogViewModel(ToolApprovalRequest request)
    {
        _request = request;
        InputText = FormatInput(request.Input);
    }

    private static string FormatInput(Dictionary<string, object>? input)
    {
        if (input is null || input.Count == 0) return "（无参数）";
        try
        {
            return JsonSerializer.Serialize(input, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return input.ToString() ?? string.Empty;
        }
    }

    [RelayCommand]
    private void Approve()
    {
        Result = new ToolApprovalDecision(_request.RequestId, Approved: true, Reason: null, AlwaysApprove);
        Finished?.Invoke();
    }

    [RelayCommand]
    private void Reject()
    {
        Result = new ToolApprovalDecision(_request.RequestId, Approved: false, Reason: "用户拒绝", AlwaysApprove: false);
        Finished?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Finished?.Invoke();
    }
}
