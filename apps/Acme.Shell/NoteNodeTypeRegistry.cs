using Acme.Notes.Domain;

namespace Acme.Shell;

/// <summary>
/// 节点类型注册表：登记各类 <see cref="NoteNodeType"/> 的描述符。
/// 调用方通过 <see cref="Get(NoteNodeType)"/> 查找，避免散落的 switch。
/// 新增类型在此登记一行即可；未知类型回退到 <see cref="NoteNodeType.Note"/>。
/// </summary>
public static class NoteNodeTypeRegistry
{
    public static IReadOnlyList<NoteNodeTypeDescriptor> All { get; }

    public static NoteNodeTypeDescriptor Get(NoteNodeType type) =>
        All.FirstOrDefault(d => d.Type == type) ?? All[0];

    static NoteNodeTypeRegistry()
    {
        All = new List<NoteNodeTypeDescriptor>
        {
            new(NoteNodeType.Note,     "文本",     NavIcons.FileText, "#6B6B70", null,   "新笔记",   ""),
            new(NoteNodeType.Agent,    "Agent",    NavIcons.Bot,      "#5B5FC7", "yaml", "新 Agent", "# Agent 模板\n\n系统提示词..."),
            new(NoteNodeType.Workflow, "Workflow", NavIcons.Workflow, "#1F8A4C", "json", "新专家团", "# Workflow 模板\n\n"),
        };
    }
}
