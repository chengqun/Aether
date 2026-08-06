using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Notes.Infrastructure.Agents;

/// <summary>
/// <see cref="IRuleSourceParser"/> 实现：规则笔记 = 单个 YAML 文档（元数据 + 规则程序顶层共存，无 front-matter 分隔、无 engine）。
/// 用 YamlDotNet 解析（与 <see cref="KnowledgeSourceParser"/> 同 builder 配置：CamelCase + IgnoreUnmatched）。
/// <para>元数据（name/description/category/tools/tool_approvals/memory/max_history/model）映射到 <see cref="CustomAgentDefinition"/> 字段；
/// 程序部分（steps/noMatchReply/maxSteps）回填到 <see cref="CustomAgentDefinition.SystemPrompt"/>（独立序列化，供
/// <c>RuleProgramYaml</c> 解析成 <c>RuleProgram</c>）。<c>Engine</c> 恒为 "Rule"。</para>
/// <para>层ing 约束：Notes.Infrastructure 不能引 <c>Aether.AI.Core</c> 的 <c>RuleProgram</c>，故这里重复一份小 step DTO（与
/// <c>RuleProgramYaml</c> 的 DTO 结构相同，保证往返一致）。</para>
/// </summary>
public sealed class RuleSourceParser : IRuleSourceParser
{
    private static readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public bool TryParse(string source, out CustomAgentDefinition? definition, out string error)
    {
        definition = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            error = "源文本为空";
            return false;
        }

        RuleDoc doc;
        try
        {
            doc = _deserializer.Deserialize<RuleDoc>(source) ?? new RuleDoc();
        }
        catch (Exception ex)
        {
            error = "规则 YAML 解析失败：" + ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(doc.Name))
        {
            error = "缺少 name 字段";
            return false;
        }

        // 程序部分：单独序列化 steps/noMatchReply/maxSteps，供 RuleProgramYaml 解析。
        var prog = new RuleProgramDoc
        {
            Steps = doc.Steps,
            NoMatchReply = doc.NoMatchReply,
            MaxSteps = doc.MaxSteps,
        };
        var programYaml = _serializer.Serialize(prog).TrimEnd();

        definition = new CustomAgentDefinition
        {
            Name = doc.Name!,
            Description = doc.Description ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(doc.Category) ? "规则" : doc.Category!,
            SystemPrompt = programYaml,
            Engine = "Rule",
            NodeType = NoteNodeType.Rule,
            ModelId = doc.ModelId ?? string.Empty,
            Tools = doc.Tools ?? new List<string>(),
            ToolApprovals = doc.ToolApprovals ?? new Dictionary<string, string>(),
            Memory = doc.Memory ?? AgentMemoryMode.Off,
            MaxHistory = doc.MaxHistory ?? 20,
            RequireToolApproval = doc.RequireToolApproval ?? false,
        };
        return true;
    }

    public string Serialize(CustomAgentDefinition def)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));

        var doc = new RuleDoc
        {
            Name = def.Name,
            Description = def.Description,
            Category = def.Category,
            Tools = def.Tools?.ToList() ?? new List<string>(),
            ToolApprovals = (def.ToolApprovals is { Count: > 0 }) ? def.ToolApprovals : null,
            RequireToolApproval = def.RequireToolApproval ? true : null,
            Memory = def.Memory,
            MaxHistory = def.MaxHistory,
            ModelId = string.IsNullOrEmpty(def.ModelId) ? null : def.ModelId,
        };

        // 程序部分：从 SystemPrompt（已是程序 YAML）解析回 step DTO，合并进同一文档。
        if (!string.IsNullOrWhiteSpace(def.SystemPrompt))
        {
            try
            {
                var prog = _deserializer.Deserialize<RuleProgramDoc>(def.SystemPrompt);
                if (prog is not null)
                {
                    doc.Steps = prog.Steps;
                    doc.NoMatchReply = prog.NoMatchReply;
                    doc.MaxSteps = prog.MaxSteps;
                }
            }
            catch
            {
                // 程序部分解析失败：保留元数据，程序部分留空（不阻塞保存）。
            }
        }

        return _serializer.Serialize(doc).TrimEnd();
    }

    // —— YAML 面 DTO（camelCase，IgnoreUnmatchedProperties 容忍多余键）——

    private sealed class RuleDoc
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public List<string>? Tools { get; set; }
        public Dictionary<string, string>? ToolApprovals { get; set; }
        public bool? RequireToolApproval { get; set; }
        public AgentMemoryMode? Memory { get; set; }
        public int? MaxHistory { get; set; }
        public string? ModelId { get; set; }
        public string? NoMatchReply { get; set; }
        public int? MaxSteps { get; set; }
        public List<RuleStepDto>? Steps { get; set; }
    }

    /// <summary>程序部分子集（仅 steps/noMatchReply/maxSteps），用于 SystemPrompt 往返。</summary>
    private sealed class RuleProgramDoc
    {
        public List<RuleStepDto>? Steps { get; set; }
        public string? NoMatchReply { get; set; }
        public int? MaxSteps { get; set; }
    }

    private sealed class RuleStepDto
    {
        public string? Name { get; set; }
        public RuleMatchDto? Match { get; set; }
        public RuleActionDto? Action { get; set; }
    }

    private sealed class RuleMatchDto
    {
        public string? OnUserRegex { get; set; }
        public string? OnResultRegex { get; set; }
        public int? MinToolCalls { get; set; }
        public int? MaxToolCalls { get; set; }
    }

    private sealed class RuleActionDto
    {
        public string? Tool { get; set; }
        public Dictionary<string, object?>? Args { get; set; }
        public string? ReplyText { get; set; }
    }
}
