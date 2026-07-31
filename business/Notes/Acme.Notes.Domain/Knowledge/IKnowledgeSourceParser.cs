namespace Acme.Notes.Domain.Knowledge;

/// <summary>
/// 知识对象源文本（YAML）解析端口。Domain 定义抽象，实现由 Infrastructure 提供
/// （YamlDotNet）。UI/Application 只依赖此抽象。
/// </summary>
public interface IKnowledgeSourceParser
{
    /// <summary>尝试把 YAML 源文本解析为 <see cref="KnowledgeObjectDefinition"/>。</summary>
    bool TryParse(string source, out KnowledgeObjectDefinition? definition, out string error);

    /// <summary>把 <see cref="KnowledgeObjectDefinition"/> 序列化回 YAML 源文本。</summary>
    string Serialize(KnowledgeObjectDefinition definition);
}
