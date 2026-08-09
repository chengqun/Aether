using Acme.Notes.Domain;

namespace Acme.Desktop.Mvvm;

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
    string? IconKey,
    string? Syntax,
    string DefaultTitle,
    string DefaultContent,
    /// <summary>有右栏表单（Agent/Rule/CodeRule/Workflow/Knowledge）；false=纯代码/纯文本全宽编辑器（Note/策略族）。</summary>
    bool IsFormNode,
    /// <summary>源码直改后失效 <c>ICustomAgentRunner</c> 缓存（Agent/Rule/CodeRule/Workflow）。Knowledge/策略族/Note 无 agent 运行时。</summary>
    bool HasAgentRunner,
    /// <summary>可召唤到聊天（Agent/Rule/CodeRule/Workflow）。Knowledge/策略族/Note 非聊天目标。</summary>
    bool IsChatTarget)
{
    /// <summary>是否强制语法（非空=覆盖节点自身 Syntax，如 Agent=yaml；null=跟随节点 Syntax）。</summary>
    public bool HasForcedSyntax => Syntax is not null;
}
