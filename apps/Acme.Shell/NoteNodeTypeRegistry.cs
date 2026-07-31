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
            new(NoteNodeType.Note,     "文本",      NavIcons.FileText, "#6B6B70", null,         null,   "新笔记",   ""),
            new(NoteNodeType.Agent,    "Agent",     NavIcons.Bot,      "#5B5FC7", "bot",        "yaml", "新 Agent", "# Agent 模板\n\n系统提示词..."),
            new(NoteNodeType.Workflow, "Workflow",  NavIcons.Workflow, "#1F8A4C", "workflow",   "json", "新专家团", "# Workflow 模板\n\n"),
            new(NoteNodeType.Knowledge, "知识对象", NavIcons.Knowledge, "#0891B2", "knowledge", "yaml", "新知识对象", KnowledgeTemplate),
        };
    }

    /// <summary>
    /// KnowledgeObject 默认模板（YAML，camelCase，与 KnowledgeSourceParser 往返一致）。
    /// 结构对应设计文档 §7：Name/DisplayName/Kind/Meaning/Properties/Relations/DataBinding/Behavior/History。
    /// </summary>
    private const string KnowledgeTemplate = @"# KnowledgeObject 定义
kind: Entity  # Entity | Metric | Rule | Process | Action | Concept
name: 新知识对象
displayName:
meaning:
properties: []
  # - name:
  #   type: string | number | enum | reference
  #   description:
relations: []
  # - target:        # 关联的另一个知识对象名
  #   type: owns     # owns | generates | affects | references
  #   description:
dataBinding:
  # source:
  # table:
behavior: []
  # - name:
  #   type: query    # query | action
  #   expression:
history: []
";
}
