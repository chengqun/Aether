using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// YAML 声明式策略的结构化编辑模型（Trading 无关的纯 .NET 类型）。行类型本身即数据载体（可观测 POCO）：
/// <c>IStrategyFormCodec</c>（Stockapp host 实现）直接读写行实例，VM 复用并订阅其变更。故 Mvvm 不引 Trading。
/// 字段命名与 <c>StrategyProgramYaml</c> 的 YAML schema 对齐（params/indicators/rules/default）。
/// </summary>
public sealed class StrategyFormModel
{
    public string Name { get; set; } = string.Empty;
    public IList<ParamRow> Params { get; } = new List<ParamRow>();
    public IList<IndicatorRow> Indicators { get; } = new List<IndicatorRow>();
    public IList<RuleRow> Rules { get; } = new List<RuleRow>();
    public string Default { get; set; } = "Hold";

    /// <summary>指标类型选项（与 IndicatorFactory 支持集对齐，去掉别名 bb/stoch/kdj 用主名）。</summary>
    public static readonly string[] IndicatorTypes =
        { "sma", "ema", "rsi", "atr", "macd", "bollinger", "stochastic" };

    /// <summary>信号选项（SignalDirection: Long/Flat/Hold）。</summary>
    public static readonly string[] SignalOptions = { "Long", "Flat", "Hold" };

    /// <summary>默认信号选项。</summary>
    public static readonly string[] DefaultOptions = { "Hold", "Long", "Flat" };

    /// <summary>比较/穿越算子（when 子句的 op）。</summary>
    public static readonly string[] ComparisonOps =
        { "gt", "lt", "gte", "lte", "cross_above", "cross_below" };

    /// <summary>指标输出名选项（空串=取主输出；其余为多输出指标的命名输出）。</summary>
    public static readonly string[] OutputNames =
        { "", "value", "upper", "middle", "lower", "macd", "signal", "hist", "k", "d" };

    /// <summary>规则组合器选项。</summary>
    public static readonly CombinerOption[] CombinerOptions =
    {
        new("None", "单条件"),
        new("And", "全部满足 (AND)"),
        new("Or", "任一满足 (OR)"),
    };

    /// <summary>操作数类型选项（Indicator=指标引用，Number=数字字面量）。</summary>
    public static readonly OperandKindOption[] OperandKindOptions =
    {
        new("Indicator", "指标"),
        new("Number", "数字"),
    };
}

/// <summary>规则组合器选项（Value=模型值，Display=UI 显示）。</summary>
public sealed record CombinerOption(string Value, string Display);

/// <summary>操作数类型选项（Value=模型值，Display=UI 显示）。</summary>
public sealed record OperandKindOption(string Value, string Display);

/// <summary>参数定义行（UI 据此渲染回测页参数输入框）。</summary>
public sealed partial class ParamRow : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private string _defaultValue = string.Empty;

    /// <summary>字段变更回调（VM 订阅 → 自动保存）。null=未订阅。</summary>
    internal EventHandler? _changed;
    partial void OnKeyChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnLabelChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnDefaultValueChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>指标参数键值行（如 period={FastPeriod}）。</summary>
public sealed partial class IndicatorArgRow : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private string _value = string.Empty;

    internal EventHandler? _changed;
    partial void OnKeyChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnValueChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>命名指标声明行（name → type + 参数）。</summary>
public sealed partial class IndicatorRow : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _type = "sma";
    public ObservableCollection<IndicatorArgRow> Args { get; } = new();

    internal EventHandler? _changed;
    partial void OnNameChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnTypeChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>操作数模型：指标引用 或 数字字面量。</summary>
public sealed partial class OperandModel : ObservableObject
{
    /// <summary>"Indicator" 或 "Number"。</summary>
    [ObservableProperty] private string _kind = "Indicator";
    [ObservableProperty] private string _indicatorName = string.Empty;
    /// <summary>指标命名输出（空=主输出）。仅在 Kind=Indicator 时有意义。</summary>
    [ObservableProperty] private string _outputName = string.Empty;
    /// <summary>数字字面量（仅在 Kind=Number 时有意义）。</summary>
    [ObservableProperty] private string _numberText = string.Empty;

    public bool IsIndicator => Kind == "Indicator";
    public bool IsNumber => Kind == "Number";

    internal EventHandler? _changed;
    partial void OnKindChanged(string value)
    {
        OnPropertyChanged(nameof(IsIndicator));
        OnPropertyChanged(nameof(IsNumber));
        _changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnIndicatorNameChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnOutputNameChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnNumberTextChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>条件子句行：左操作数 op 右操作数（扁平，不递归）。</summary>
public sealed partial class ClauseRow : ObservableObject
{
    public OperandModel Left { get; } = new();
    [ObservableProperty] private string _op = "cross_above";
    public OperandModel Right { get; } = new();

    internal EventHandler? _changed;
    partial void OnOpChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 规则行：信号 + 组合器 + 若干子句。
/// <para><see cref="IsAdvanced"/>=true 时表单不可编辑该规则的条件（<see cref="RawWhenYaml"/> 原样保留），
/// 用于 not / 深层嵌套等扁平模型装不下的情况——不丢数据，用户在下栏源码改。</para>
/// </summary>
public sealed partial class RuleRow : ObservableObject
{
    [ObservableProperty] private string _signal = "Long";
    /// <summary>"None"/"And"/"Or"。</summary>
    [ObservableProperty] private string _combiner = "None";
    public ObservableCollection<ClauseRow> Clauses { get; } = new();

    [ObservableProperty] private bool _isAdvanced;
    [ObservableProperty] private string _rawWhenYaml = string.Empty;

    public bool IsSimple => !IsAdvanced;

    internal EventHandler? _changed;
    partial void OnSignalChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnCombinerChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
    partial void OnIsAdvancedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSimple));
        _changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnRawWhenYamlChanged(string value) => _changed?.Invoke(this, EventArgs.Empty);
}
