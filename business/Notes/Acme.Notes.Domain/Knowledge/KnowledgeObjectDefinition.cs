namespace Acme.Notes.Domain.Knowledge;

/// <summary>
/// 知识对象的一条 typed 引用（图边）。role 自由字符串（uses/then/applies-to/produces/depends-on…，只写正向依赖边，不写反向回指边）。
/// <see cref="TargetId"/> 为目标笔记 Id（权威引用）；<see cref="Target"/> 为标题（显示/回退）。
/// 旧数据只有标题 → TargetId 为 null，读取时回退到标题匹配。
/// </summary>
public sealed class KnowledgeLink
{
    public string Role { get; set; } = string.Empty;

    /// <summary>目标笔记 Id（权威引用）。旧数据/未解析时为 null。</summary>
    public Guid? TargetId { get; set; }

    /// <summary>目标笔记标题（显示/回退）。编辑时按标题解析出 TargetId。</summary>
    public string Target { get; set; } = string.Empty;
}

/// <summary>
/// 知识对象定义（最简通用模型）：kind + typed links + markdown 正文。
/// 与 Agent 笔记同构（front-matter + 正文）。name = 笔记标题（不在内容里）。
/// 结构化数据放正文当文本，AI 读正文 + 跟 links 取子笔记。
/// </summary>
public sealed class KnowledgeObjectDefinition
{
    /// <summary>分类（自由字符串：Entity/Rule/情报案件/…）。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>typed 引用列表。</summary>
    public List<KnowledgeLink> Links { get; set; } = new();

    /// <summary>markdown 正文（人类说明 / 结构化数据代码块 / 任何 markdown）。</summary>
    public string Body { get; set; } = string.Empty;
}
