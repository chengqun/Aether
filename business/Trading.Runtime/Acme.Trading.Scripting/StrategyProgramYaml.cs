using System.Collections;
using System.Globalization;
using Acme.Trading.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Acme.Trading.Scripting;

/// <summary>
/// 声明式策略 YAML ↔ <see cref="StrategyProgram"/>（Trading.Domain DTO）转换。
/// 仅此脚本层引 YamlDotNet——<c>Trading.Domain</c> 保持纯净不引。镜像 <c>RuleProgramYaml</c> 的做法：
/// 反序列化进私有 mirror DTO，递归规整 YamlDotNet 松散类型，再 Map 到 Core DTO。
/// <para>条件树（<c>when</c>）反序列化为原始对象图，经 <see cref="ConditionDef.TryParse"/> 解析成 AST
/// （纯 .NET 类型操作，在 Domain 内，可脱库测试）。</para>
/// </summary>
internal static class StrategyProgramYaml
{
    private static readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>尝试把 YAML 策略文本解析成 <see cref="StrategyProgram"/>。空/异常→false + null。</summary>
    public static bool TryDeserialize(string? yaml, out StrategyProgram? program)
    {
        program = null;
        if (string.IsNullOrWhiteSpace(yaml)) return false;
        try
        {
            var dto = _yaml.Deserialize<ProgramDto>(yaml);
            if (dto is null) return false;
            program = Map(dto);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static StrategyProgram Map(ProgramDto dto)
    {
        var @params = (dto.Params ?? new()).Select(p => new StrategyParameterDef(
            p.Key ?? "", p.Label ?? p.Key ?? "", p.Default ?? "")).ToList();

        var indicators = new Dictionary<string, IndicatorDef>(System.StringComparer.OrdinalIgnoreCase);
        if (dto.Indicators is { Count: > 0 } inds)
        {
            foreach (var kv in inds)
            {
                // 指标对象：type 是类型，其余键都是参数（扁平 schema：{ type: sma, period: 2 }）。
                var args = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
                string type = "";
                if (kv.Value is { } indMap)
                {
                    foreach (var ak in indMap)
                    {
                        if (string.Equals(ak.Key, "type", System.StringComparison.OrdinalIgnoreCase))
                            type = ak.Value?.ToString() ?? "";
                        else
                            args[ak.Key] = Stringify(ak.Value);
                    }
                }
                indicators[kv.Key] = new IndicatorDef(type, args);
            }
        }

        var rules = new List<StrategyRuleDef>();
        foreach (var r in dto.Rules ?? new())
        {
            if (r.When is null) continue;
            if (!ConditionDef.TryParse(Normalize(r.When), out var cond)) continue;
            rules.Add(new StrategyRuleDef(ParseSignal(r.Signal), cond!));
        }

        return new StrategyProgram(@params, indicators, rules, ParseSignal(dto.Default));
    }

    private static SignalDirection ParseSignal(string? s)
        => System.Enum.TryParse<SignalDirection>(s, ignoreCase: true, out var v) ? v : SignalDirection.Hold;

    /// <summary>把任意标量/对象规整成干净 .NET 类型并字符串化（指标参数值）。</summary>
    private static string Stringify(object? v) => v switch
    {
        null => "",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal de => de.ToString(CultureInfo.InvariantCulture),
        long or int or short or byte => System.Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        string s => s,
        _ => v.ToString() ?? "",
    };

    /// <summary>递归规整 YamlDotNet 松散类型（镜像 RuleProgramYaml.Normalize）。
    /// Dictionary&lt;object,object&gt;→Dictionary&lt;string,object?&gt;、IList→List&lt;object?&gt;、标量原样。</summary>
    private static object? Normalize(object? v) => v switch
    {
        null => null,
        IDictionary d => NormalizeDict(d),
        IList l => NormalizeList(l),
        _ => v,
    };

    private static Dictionary<string, object?> NormalizeDict(IDictionary d)
    {
        var result = new Dictionary<string, object?>(d.Count);
        foreach (DictionaryEntry kv in d) result[kv.Key?.ToString() ?? ""] = Normalize(kv.Value);
        return result;
    }

    private static List<object?> NormalizeList(IList l)
    {
        var result = new List<object?>(l.Count);
        foreach (var item in l) result.Add(Normalize(item));
        return result;
    }

    // —— YAML 面 DTO（camelCase，IgnoreUnmatchedProperties 容忍多余键）——

    private sealed class ProgramDto
    {
        public List<ParamDto>? Params { get; set; }
        public Dictionary<string, Dictionary<string, object?>>? Indicators { get; set; }
        public List<RuleDto>? Rules { get; set; }
        public string? Default { get; set; }
    }

    private sealed class ParamDto
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Default { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Signal { get; set; }
        /// <summary>条件 AST（原始对象图，经 Normalize + ConditionDef.TryParse 解析）。</summary>
        public object? When { get; set; }
    }
}
