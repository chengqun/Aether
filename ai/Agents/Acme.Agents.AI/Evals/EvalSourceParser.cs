using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Agents.AI.Evals;

/// <summary>
/// <c>evals/*.eval.yaml</c> 解析器：单个 YAML 文档（与 <c>RuleSourceParser</c> 同 builder 配置：
/// CamelCase 命名 + 容忍未识别键）。评测刻意不是 NoteNodeType（无表单、无 catalog 节点）——
/// 目录消费模式（镜像 <c>mcp-servers/</c>/<c>skills/</c>），parser 与 runner 同居适配器（唯一消费者），
/// 不进 Notes.Infrastructure。
/// </summary>
public sealed class EvalSourceParser
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public bool TryParse(string source, out EvalDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = "评测文件为空";
            return false;
        }

        EvalDoc doc;
        try
        {
            doc = _deserializer.Deserialize<EvalDoc>(source) ?? new EvalDoc();
        }
        catch (Exception ex)
        {
            error = "评测 YAML 解析失败：" + ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(doc.Name))
        {
            error = "缺少 name 字段";
            return false;
        }

        if (doc.AgentId is null || doc.AgentId == Guid.Empty)
        {
            error = "缺少 agentId 字段（被评 agent 的 Guid）";
            return false;
        }

        if (doc.Cases is not { Count: > 0 })
        {
            error = "缺少 cases 字段（至少一条用例）";
            return false;
        }

        for (var i = 0; i < doc.Cases.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(doc.Cases[i].Query))
            {
                error = $"cases[{i}] 缺少 query 字段";
                return false;
            }
        }

        definition = new EvalDefinition
        {
            Name = doc.Name!,
            Description = doc.Description,
            AgentId = doc.AgentId.Value,
            Cases = doc.Cases.Select(c => new EvalCase
            {
                Query = c.Query!,
                ExpectedKeywords = c.ExpectedKeywords is { Count: > 0 } ? c.ExpectedKeywords : null,
                ExpectedOutput = string.IsNullOrWhiteSpace(c.ExpectedOutput) ? null : c.ExpectedOutput,
                ExpectedTools = c.ExpectedTools is { Count: > 0 } ? c.ExpectedTools : null,
                MinLength = c.MinLength,
            }).ToList(),
        };
        return true;
    }

    // —— YAML 面 DTO（camelCase，IgnoreUnmatchedProperties 容忍多余键）——

    private sealed class EvalDoc
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public Guid? AgentId { get; set; }
        public List<EvalCaseDoc>? Cases { get; set; }
    }

    private sealed class EvalCaseDoc
    {
        public string? Query { get; set; }
        public List<string>? ExpectedKeywords { get; set; }
        public string? ExpectedOutput { get; set; }
        public List<string>? ExpectedTools { get; set; }
        public int? MinLength { get; set; }
    }
}
