namespace Acme.Notes.Domain.Agents;

/// <summary>
/// Agent 源文本（YAML front-matter + Markdown 正文）解析端口。
/// Domain 定义抽象，实现由 Infrastructure 提供。UI/Application 只依赖此抽象。
/// </summary>
public interface IAgentSourceParser
{
    /// <summary>尝试把源文本解析为 <see cref="CustomAgentDefinition"/>。</summary>
    bool TryParse(string source, out CustomAgentDefinition? definition, out string error);

    /// <summary>把 <see cref="CustomAgentDefinition"/> 序列化回源文本。</summary>
    string Serialize(CustomAgentDefinition definition);
}
