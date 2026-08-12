using System.Collections;
using System.Globalization;
using Acme.Desktop.Mvvm;
using Acme.Trading.Domain;
using Acme.Trading.Scripting;
using YamlDotNet.Serialization;

namespace Acme.Stockapp.Desktop.Hosting;

/// <summary>
/// <see cref="IStrategyFormCodec"/> 实现：YAML 策略源文本 ↔ <see cref="StrategyFormModel"/> 往返。
/// 复用 <see cref="StrategyProgramYaml.TryDeserialize"/> + <c>ConditionDef</c> AST（不重复解析逻辑）。
/// 序列化用 YamlDotNet 镜像 <c>StrategyProgramYaml</c> 的 camelCase schema（params/indicators/rules/default），
/// 产出与 <see cref="StrategyProgramYaml.TryDeserialize"/> 兼容——下栏手写或表单生成的 YAML 同样可被回测消费。
/// </summary>
public sealed class StrategyFormCodec : IStrategyFormCodec
{
    private static readonly ISerializer _serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer _fragmentReader = new DeserializerBuilder().Build();

    private static readonly HashSet<string> ComparisonOps = new(StringComparer.OrdinalIgnoreCase)
        { "gt", "lt", "gte", "ge", "lte", "le", "cross_above", "crossabove", "xabove", "cross_below", "crossbelow", "xbelow" };

    public bool TryParse(string? yaml, string name, out StrategyFormModel? model, out string? error)
    {
        if (!StrategyProgramYaml.TryDeserialize(yaml, out var prog, out error))
        {
            model = null;
            return false;
        }
        model = MapToForm(name, prog!);
        error = null;
        return true;
    }

    private static StrategyFormModel MapToForm(string name, StrategyProgram prog)
    {
        var model = new StrategyFormModel
        {
            Name = name,
            Default = prog.Default.ToString(),
        };

        foreach (var p in prog.Params)
            model.Params.Add(new ParamRow { Key = p.Key, Label = p.Label, DefaultValue = p.DefaultValue });

        foreach (var kv in prog.Indicators)
        {
            var row = new IndicatorRow { Name = kv.Key, Type = string.IsNullOrEmpty(kv.Value.Type) ? "sma" : kv.Value.Type };
            foreach (var a in kv.Value.Args)
                row.Args.Add(new IndicatorArgRow { Key = a.Key, Value = a.Value });
            model.Indicators.Add(row);
        }

        var indicatorNames = new HashSet<string>(prog.Indicators.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var r in prog.Rules)
        {
            var row = new RuleRow { Signal = r.Signal.ToString() };
            FlattenCondition(r.When, indicatorNames, row);
            model.Rules.Add(row);
        }

        return model;
    }

    /// <summary>把 ConditionDef AST 扁平化进 RuleRow；装不下的（not/深层嵌套）走 IsAdvanced 回退。</summary>
    private static void FlattenCondition(ConditionDef cond, HashSet<string> indicatorNames, RuleRow row)
    {
        var op = cond.Op ?? "";
        // 单个比较/穿越算子 → Combiner=None + 1 子句
        if (ComparisonOps.Contains(op))
        {
            if (cond.Args.Count == 2)
            {
                row.Combiner = "None";
                row.Clauses.Add(BuildClause(op, cond.Args[0], cond.Args[1], indicatorNames));
                return;
            }
            // 比较算子但参数数不对 → advanced
        }
        else if (string.Equals(op, "and", StringComparison.OrdinalIgnoreCase))
        {
            row.Combiner = "And";
            if (TryFlattenClauses(cond.Args, indicatorNames, row)) return;
        }
        else if (string.Equals(op, "or", StringComparison.OrdinalIgnoreCase))
        {
            row.Combiner = "Or";
            if (TryFlattenClauses(cond.Args, indicatorNames, row)) return;
        }
        // not / 未知 / and|or 含非比较子节点 → advanced：把 AST 还原成 YAML 片段原样保留
        row.IsAdvanced = true;
        row.RawWhenYaml = _serializer.Serialize(ConditionToObject(cond)).Trim();
    }

    /// <summary>and/or 的子节点全是比较算子 → 扁平成子句列表；任一子节点是 and/or/not → 返回 false（走 advanced）。</summary>
    private static bool TryFlattenClauses(IReadOnlyList<object> args, HashSet<string> indicatorNames, RuleRow row)
    {
        var clauses = new List<ClauseRow>();
        foreach (var arg in args)
        {
            if (arg is not ConditionDef child) return false;
            if (!ComparisonOps.Contains(child.Op ?? "")) return false;
            if (child.Args.Count != 2) return false;
            clauses.Add(BuildClause(child.Op!, child.Args[0], child.Args[1], indicatorNames));
        }
        if (clauses.Count == 0) return false;
        foreach (var c in clauses) row.Clauses.Add(c);
        return true;
    }

    private static ClauseRow BuildClause(string op, object left, object right, HashSet<string> indicatorNames)
    {
        var c = new ClauseRow { Op = NormalizeOp(op) };
        FillOperand(c.Left, left, indicatorNames);
        FillOperand(c.Right, right, indicatorNames);
        return c;
    }

    /// <summary>叶子 → OperandModel：string 命中指标名→Indicator（含 name.output 拆分）；数字→Number；否则 Indicator 存原值。</summary>
    private static void FillOperand(OperandModel m, object leaf, HashSet<string> indicatorNames)
    {
        switch (leaf)
        {
            case string s:
                var dot = s.IndexOf('.');
                if (dot > 0 && indicatorNames.Contains(s[..dot]))
                {
                    m.Kind = "Indicator";
                    m.IndicatorName = s[..dot];
                    m.OutputName = s[(dot + 1)..];
                }
                else if (indicatorNames.Contains(s))
                {
                    m.Kind = "Indicator";
                    m.IndicatorName = s;
                    m.OutputName = string.Empty;
                }
                else if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    m.Kind = "Number";
                    m.NumberText = s;
                }
                else
                {
                    // 未知字符串：当指标引用存原值（用户可在表单修正）
                    m.Kind = "Indicator";
                    m.IndicatorName = s;
                }
                break;
            case long or int or short or byte:
                m.Kind = "Number";
                m.NumberText = Convert.ToString(leaf, CultureInfo.InvariantCulture) ?? "";
                break;
            case double d:
                m.Kind = "Number";
                m.NumberText = d.ToString("R", CultureInfo.InvariantCulture);
                break;
            case float f:
                m.Kind = "Number";
                m.NumberText = f.ToString("R", CultureInfo.InvariantCulture);
                break;
            case decimal de:
                m.Kind = "Number";
                m.NumberText = de.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                m.Kind = "Number";
                m.NumberText = leaf?.ToString() ?? "";
                break;
        }
    }

    /// <summary>算子别名归一化到主名（ge→gte 等），与表单选项集对齐。</summary>
    private static string NormalizeOp(string op) => op.ToLowerInvariant() switch
    {
        "ge" => "gte",
        "le" => "lte",
        "crossabove" or "xabove" => "cross_above",
        "crossbelow" or "xbelow" => "cross_below",
        _ => op.ToLowerInvariant(),
    };

    public string Serialize(StrategyFormModel model)
    {
        var doc = new Dictionary<string, object>
        {
            ["params"] = model.Params.Select(p => new Dictionary<string, object?>
            {
                ["key"] = p.Key ?? "",
                ["label"] = p.Label ?? "",
                ["default"] = p.DefaultValue ?? "",
            }).ToList(),
            ["indicators"] = model.Indicators.ToDictionary(
                i => i.Name ?? "",
                i => BuildIndicatorMap(i)),
            ["rules"] = model.Rules.Select(r => new Dictionary<string, object?>
            {
                ["signal"] = r.Signal ?? "Hold",
                ["when"] = BuildWhen(r),
            }).ToList(),
            ["default"] = model.Default ?? "Hold",
        };
        return _serializer.Serialize(doc);
    }

    private static Dictionary<string, object> BuildIndicatorMap(IndicatorRow i)
    {
        var map = new Dictionary<string, object> { ["type"] = i.Type ?? "sma" };
        foreach (var a in i.Args)
            if (!string.IsNullOrWhiteSpace(a.Key))
                map[a.Key] = a.Value ?? "";
        return map;
    }

    private static object? BuildWhen(RuleRow r)
    {
        if (r.IsAdvanced)
        {
            // 高级规则：把原始 when YAML 片段反序列化回对象图原样嵌入（不丢数据）
            if (string.IsNullOrWhiteSpace(r.RawWhenYaml)) return null;
            try { return _fragmentReader.Deserialize<object>(r.RawWhenYaml); }
            catch { return null; }
        }

        if (r.Clauses.Count == 0) return null;

        if (r.Combiner == "And" || r.Combiner == "Or")
        {
            var key = r.Combiner == "And" ? "and" : "or";
            var children = r.Clauses.Select(c => (object)ClauseToObject(c)).ToList();
            return new Dictionary<string, object> { [key] = children };
        }

        // Combiner=None：单子句
        return ClauseToObject(r.Clauses[0]);
    }

    private static Dictionary<string, object> ClauseToObject(ClauseRow c)
        => new() { [c.Op ?? "gt"] = new List<object> { OperandToValue(c.Left), OperandToValue(c.Right) } };

    private static object OperandToValue(OperandModel m)
    {
        if (m.Kind == "Number")
        {
            var t = m.NumberText ?? "";
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return t;
        }
        // Indicator
        var name = m.IndicatorName ?? "";
        return string.IsNullOrEmpty(m.OutputName) ? name : $"{name}.{m.OutputName}";
    }

    /// <summary>ConditionDef AST → YAML 对象图（用于高级规则的 RawWhenYaml 还原）。</summary>
    private static object ConditionToObject(ConditionDef cond)
        => new Dictionary<string, object> { [cond.Op ?? "gt"] = cond.Args.Select(ArgToObject).ToList() };

    private static object ArgToObject(object? arg)
        => arg is ConditionDef nested ? ConditionToObject(nested) : (object?)NormalizeScalar(arg);

    private static object? NormalizeScalar(object? v) => v switch
    {
        null => "",
        bool or string or long or int or double or float or decimal => v,
        _ => v.ToString() ?? "",
    };
}
