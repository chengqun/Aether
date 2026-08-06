namespace Acme.Notes.Domain.Agents;

/// <summary>
/// 规则源文本（单个 YAML 文档：元数据 + 规则程序顶层共存）解析端口。
/// 与 <see cref="IAgentSourceParser"/> 平级——规则笔记不再走 agent 的 front-matter+body 格式，
/// 而是单个 YAML 文档（name/tools/steps/noMatchReply/maxSteps 等全在顶层，无 <c>---</c> 分隔、无 <c>engine:</c>）。
/// Domain 定义抽象，实现由 Infrastructure 提供。UI/Application 只依赖此抽象。
/// </summary>
public interface IRuleSourceParser
{
    /// <summary>尝试把规则源文本解析为 <see cref="CustomAgentDefinition"/>（<c>Engine</c> 恒为 "Rule"；<c>SystemPrompt</c>=规则程序部分 YAML）。</summary>
    bool TryParse(string source, out CustomAgentDefinition? definition, out string error);

    /// <summary>把 <see cref="CustomAgentDefinition"/> 序列化回单个 YAML 文档（元数据 + 程序合并）。</summary>
    string Serialize(CustomAgentDefinition definition);
}
