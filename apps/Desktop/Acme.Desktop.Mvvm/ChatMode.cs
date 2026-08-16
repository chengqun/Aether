namespace Acme.Desktop.Mvvm;

/// <summary>聊天输入框的 4 档运行模式（对标 Claude Code 的 Plan/Manual/Edit-automatically/Auto）。
/// MAF 只存 plan/execute 两态（<see cref="IsPlan"/> → desiredMode）；execute 子档（Manual/AutoEdit/Auto）
/// 是纯 VM 状态，经审批拦截（VM 层 OnApprovalRequest）逐工具决定自动批/弹框。详见 ChatWorkspaceViewModel。</summary>
public enum ChatMode
{
    /// <summary>规划：AgentMode=plan，agent 先出计划不执行；产出计划后弹「接受计划→执行」。</summary>
    Plan,
    /// <summary>手动：AgentMode=execute；每个非 Safe 工具都弹审批框。</summary>
    Manual,
    /// <summary>自动编辑（默认）：AgentMode=execute；自动批 Confirm 级工具，Dangerous 仍弹框。</summary>
    AutoEdit,
    /// <summary>自动：AgentMode=execute；全部自动批（含 Dangerous），不弹框。</summary>
    Auto,
}

public static class ChatModeExtensions
{
    /// <summary>是否为规划档（对应 MAF AgentMode=plan；其余三档为 execute）。</summary>
    public static bool IsPlan(this ChatMode mode) => mode == ChatMode.Plan;

    /// <summary>映射到 MAF desiredMode 字符串（"plan"/"execute"）。</summary>
    public static string ToDesiredMode(this ChatMode mode) => mode == ChatMode.Plan ? "plan" : "execute";

    /// <summary>从 MAF 读回的 mode 字符串判断是否为 plan。</summary>
    public static bool IsPlanMode(string? mafMode) => mafMode == "plan";
}
