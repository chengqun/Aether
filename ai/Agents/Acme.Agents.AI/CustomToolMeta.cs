namespace Acme.Agents.AI;

/// <summary>
/// UI 无关的工具元数据快照（名称 + 描述 + 分类 + 审批等级标签），供专家编辑页勾选展示。
/// 从 <c>Aether.AI.Harness.Tools.ToolMeta</c> 映射而来，使 VM 层不必引用 Harness。
/// <see cref="ApprovalLevel"/> 为工具声明的等级名："Safe"/"Confirm"/"Dangerous"（专家编辑页可按工具覆盖）。
/// </summary>
public sealed record CustomToolMeta(string Name, string Description, string Category, string ApprovalLevel = "Safe");
