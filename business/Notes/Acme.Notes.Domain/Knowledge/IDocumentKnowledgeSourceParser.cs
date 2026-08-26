namespace Acme.Notes.Domain.Knowledge;

/// <summary>
/// 文档版（<c>.md</c> 文件）知识对象源码解析器。与 note 版 <see cref="IKnowledgeSourceParser"/> 并列，
/// **不复用** note 版实现。原因：文档版 link 的 <c>targetId</c> 是指向项目内另一个 <c>.md</c> 文件的
/// **相对路径字符串**（如 <c>Hive表/fin_dw.dwd_xxx</c>），而 note 版 <see cref="KnowledgeLink.TargetId"/>
/// 是 <see cref="Guid"/>?（note 节点 id）。类型不兼容——YamlDotNet 无法把路径字符串反序列化进 <c>Guid?</c>，
/// 会导致整个 front-matter 解析失败（note 版解析器对文档版 .md 返回 false）。
/// 复用 <see cref="KnowledgeObjectDefinition"/> 作为内存模型：相对路径存入 <see cref="KnowledgeLink.Target"/>，
/// <see cref="KnowledgeLink.TargetId"/> 留 <c>null</c>。序列化把路径写回 <c>targetId</c> 字段，
/// 保持用户既有 <c>.md</c> 文件格式——相对路径即指针，无需额外字典映射。
/// </summary>
public interface IDocumentKnowledgeSourceParser
{
    bool TryParse(string source, out KnowledgeObjectDefinition? definition, out string error);
    string Serialize(KnowledgeObjectDefinition definition);
}
