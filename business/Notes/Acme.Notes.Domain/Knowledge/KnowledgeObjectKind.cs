namespace Acme.Notes.Domain.Knowledge;

/// <summary>
/// 知识对象类型（设计文档 §8）：实体 / 指标 / 规则 / 流程 / 动作 / 概念。
/// <see cref="Unknown"/> 为解析回退（源文本中既非空又无法识别时）。
/// </summary>
public enum KnowledgeObjectKind
{
    Unknown = 0,
    Entity = 1,
    Metric = 2,
    Rule = 3,
    Process = 4,
    Action = 5,
    Concept = 6,
}
