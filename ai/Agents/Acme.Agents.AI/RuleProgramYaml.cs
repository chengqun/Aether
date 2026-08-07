using System.Collections;
using Aether.AI.Core.Agents;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Agents.AI;

/// <summary>
/// 规则程序 YAML ↔ <see cref="RuleProgram"/>（Core DTO）转换。仅此桥接适配器引 YamlDotNet——
/// <c>src/</c> 平台层不引。规则 agent 的笔记 body 是 YAML 程序，这里解析成 <see cref="RuleProgram"/>
/// <strong>对象</strong>直接喂平台 seam（<see cref="AgentDefinition.Rules"/>），全程不经 JSON 中转。
/// <para>YAML 面 <c>args</c> 是结构化 map（如 <c>args: {text: "{user.1}"}</c>），映射到
/// <see cref="RuleAction.Args"/>（<c>Dictionary&lt;string,object?&gt;</c>）：递归规整 YamlDotNet 松散类型后
/// 直接作为对象传递。模板占位符（<c>{user.N}</c>/<c>{result}</c>）原样保留，替换在
/// <see cref="Aether.AI.Harness.Agents.RuleProgramBrain"/> 运行时按值做。</para>
/// </summary>
internal static class RuleProgramYaml
{
    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>尝试把 YAML 程序文本解析成 <see cref="RuleProgram"/>。空/异常→false + null。</summary>
    public static bool TryDeserialize(string? yaml, out RuleProgram? program)
    {
        program = null;
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        try
        {
            var dto = _yaml.Deserialize<RuleProgramDto>(yaml);
            if (dto is null) return false;
            program = Map(dto);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RuleProgram Map(RuleProgramDto dto) => new()
    {
        Name = dto.Name ?? string.Empty,
        Steps = (dto.Steps ?? new()).Select(MapStep).ToList(),
        NoMatchReply = dto.NoMatchReply,
        MaxSteps = dto.MaxSteps > 0 ? dto.MaxSteps : 20,
        Capture = dto.Capture,
    };

    private static RuleStep MapStep(RuleStepDto s) => new()
    {
        Name = s.Name,
        Match = MapMatch(s.Match),
        Action = MapAction(s.Action),
    };

    private static RuleMatch MapMatch(RuleMatchDto? m) => new()
    {
        OnUserRegex = m?.OnUserRegex,
        OnResultRegex = m?.OnResultRegex,
        MinToolCalls = m?.MinToolCalls,
        MaxToolCalls = m?.MaxToolCalls,
    };

    private static RuleAction MapAction(RuleActionDto? a)
    {
        var action = new RuleAction
        {
            Tool = a?.Tool,
            ReplyText = a?.ReplyText,
        };
        if (a?.Args is { Count: > 0 } args)
        {
            action.Args = NormalizeDict(args);
        }
        return action;
    }

    /// <summary>递归规整 YamlDotNet 松散类型为干净 .NET 类型：
    /// <c>Dictionary&lt;object,object&gt;</c>→<c>Dictionary&lt;string,object?&gt;</c>、<c>IList</c>→<c>List&lt;object?&gt;</c>、标量原样。
    /// 不再经 JSON 中转——对象直接作为 <see cref="RuleAction.Args"/> 透传给规则大脑。</summary>
    private static object? Normalize(object? v) => v switch
    {
        null => null,
        IDictionary d => NormalizeDict(d),
        IList l => NormalizeList(l),
        string or bool or long or int or double or float or decimal => v,
        // YamlDotNet 可能产出其它标量（DateTime 等）；原样透传
        _ => v,
    };

    private static Dictionary<string, object?> NormalizeDict(IDictionary d)
    {
        var result = new Dictionary<string, object?>(d.Count);
        foreach (DictionaryEntry kv in d)
        {
            result[kv.Key?.ToString() ?? ""] = Normalize(kv.Value);
        }
        return result;
    }

    private static List<object?> NormalizeList(IList l)
    {
        var result = new List<object?>(l.Count);
        foreach (var item in l) result.Add(Normalize(item));
        return result;
    }

    // —— YAML 面 DTO（camelCase，IgnoreUnmatchedProperties 容忍多余键）——

    private sealed class RuleProgramDto
    {
        public string? Name { get; set; }
        public List<RuleStepDto>? Steps { get; set; }
        public string? NoMatchReply { get; set; }
        public int MaxSteps { get; set; } = 20;
        public string? Capture { get; set; }
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
        /// <summary>结构化工具参数（YAML map）。规整后映射到 <see cref="RuleAction.Args"/>。</summary>
        public Dictionary<string, object?>? Args { get; set; }
        public string? ReplyText { get; set; }
    }
}
