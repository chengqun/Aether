namespace Acme.Notes.Domain.Knowledge;

/// <summary>知识对象属性（设计文档 §7 Properties）。</summary>
public sealed class KnowledgeProperty
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>知识对象关系（设计文档 §7 Relations / §10 Ontology）。</summary>
public sealed class KnowledgeRelation
{
    public string Target { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>知识对象数据绑定（设计文档 §7 Data Binding / §11 语义层）。</summary>
public sealed class KnowledgeDataBinding
{
    public string Source { get; set; } = string.Empty;
    public string Table { get; set; } = string.Empty;
}

/// <summary>知识对象行为/能力（设计文档 §7 Behavior）。</summary>
public sealed class KnowledgeBehavior
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
}

/// <summary>
/// 知识对象定义（设计文档 §7）。一个 KnowledgeObject 表示现实世界中的
/// 实体/指标/规则/流程/动作/概念。纯 POCO，作为 YAML 解析目标与 UI 编辑模型。
/// </summary>
public sealed class KnowledgeObjectDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public KnowledgeObjectKind Kind { get; set; } = KnowledgeObjectKind.Entity;
    public string Meaning { get; set; } = string.Empty;
    public List<KnowledgeProperty> Properties { get; set; } = new();
    public List<KnowledgeRelation> Relations { get; set; } = new();
    public KnowledgeDataBinding? DataBinding { get; set; }
    public List<KnowledgeBehavior> Behavior { get; set; } = new();
    public List<string> History { get; set; } = new();
}
