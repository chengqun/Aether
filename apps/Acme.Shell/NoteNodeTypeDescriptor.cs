using Acme.Notes.Domain;

namespace Acme.Shell;

/// <summary>
/// 节点类型描述符：聚合一个 <see cref="NoteNodeType"/> 的显示 + 创建 + 语法行为，
/// 供 <see cref="NoteNodeTypeRegistry"/> 统一查找，避免在各调用点写 switch。
/// 新增节点类型只需登记一项，不必改动既有 switch 站点。
/// </summary>
public sealed record NoteNodeTypeDescriptor(
    NoteNodeType Type,
    string Display,
    string IconPathData,
    string IconColor,
    string? Syntax,
    string DefaultTitle,
    string DefaultContent)
{
    /// <summary>是否强制语法（非空=覆盖节点自身 Syntax，如 Agent=yaml；null=跟随节点 Syntax）。</summary>
    public bool HasForcedSyntax => Syntax is not null;
}
