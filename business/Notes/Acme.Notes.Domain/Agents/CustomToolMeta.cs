namespace Acme.Notes.Domain.Agents;

/// <summary>
/// UI 无关的工具元数据快照（名称 + 描述 + 分类 + 审批等级标签 + 宿主能力标签），供专家编辑页勾选展示。
/// 从 <c>Aether.AI.Harness.Tools.ToolMeta</c> 映射而来，使 VM 层不必引用 Harness。
/// <see cref="ApprovalLevel"/> 为工具声明的等级名："Safe"/"Confirm"/"Dangerous"（专家编辑页可按工具覆盖）。
/// <see cref="HostCapability"/> 为工具声明的宿主能力名："All"/"DesktopOnly"/"MobileOnly"
/// （编辑页据此置灰「当前平台不可用」的工具）。
/// </summary>
public sealed record CustomToolMeta(string Name, string Description, string Category, string ApprovalLevel = "Safe", string HostCapability = "All");
