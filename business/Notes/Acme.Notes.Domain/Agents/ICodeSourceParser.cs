namespace Acme.Notes.Domain.Agents;

/// <summary>
/// 代码规则源文本解析端口：YAML front-matter（元数据）+ 原始 C# 正文（<c>CodeRuleBrain</c> 实现类源码）。
/// 与 <see cref="IAgentSourceParser"/> 同构（front-matter + body），区别在 body 是 C# 而非 markdown，
/// 且 <c>Engine</c> 恒为 "Rule"、<c>NodeType</c> 恒为 <see cref="NoteNodeType.CodeRule"/>。
/// 解析器不编译——编译由平台 <c>IRuleScriptingService</c>（Roslyn）负责，本端口只做 front-matter 拆分。
/// </summary>
public interface ICodeSourceParser
{
    /// <summary>尝试把代码规则源文本解析为 <see cref="CustomAgentDefinition"/>（<c>SystemPrompt</c>=C# 正文）。</summary>
    bool TryParse(string source, out CustomAgentDefinition? definition, out string error);

    /// <summary>把 <see cref="CustomAgentDefinition"/> 序列化回 front-matter + C# 正文。</summary>
    string Serialize(CustomAgentDefinition definition);
}
