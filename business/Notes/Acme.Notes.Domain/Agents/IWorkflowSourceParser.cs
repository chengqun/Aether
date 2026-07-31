namespace Acme.Notes.Domain.Agents;

/// <summary>
/// Workflow 源文本（JSON）解析端口。Domain 定义抽象，实现由 Infrastructure 提供。
/// </summary>
public interface IWorkflowSourceParser
{
    /// <summary>尝试把源文本解析为 <see cref="CustomWorkflowDefinition"/>。</summary>
    bool TryParse(string source, out CustomWorkflowDefinition? definition, out string error);

    /// <summary>把 <see cref="CustomWorkflowDefinition"/> 序列化回源文本。</summary>
    string Serialize(CustomWorkflowDefinition definition);
}
