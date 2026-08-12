using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;

namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// 用户 Lean 算法策略的基类（<see cref="QCAlgorithm"/> 子类）。第三种策略写法——直接复用 Lean 全部能力
/// （原生指标 SMA/RSI/MACD 链式组合、Portfolio 仓位、SetHoldings、Schedule、OnEndOfDay、History 等），
/// 绕过 <see cref="TradingHostAlgorithm"/> + <see cref="Acme.Trading.Domain.IStrategy"/> 那层壳。
/// </summary>
/// <remarks>
/// <b>数据约定（重要）</b>：本地无 Lean 原生行情数据，必须用 <see cref="AddCtpData"/> 订阅本地 CtpBar
/// （回测读预取 CSV，实盘读 CTP tick 聚合）。写 <c>AddSecurity</c>/<c>AddEquity</c> 会拿不到数据。
/// <para/>
/// <b>参数透传</b>：runner 把 start/end/cash/symbol/resolution + 用户参数设进 Lean Config "parameters"，
/// 算法经 <c>GetParameter("key")</c> 读。<see cref="ConfigureFromConfig"/> 封装了 start/end/cash 的读取 + 设置——
/// 在 <c>Initialize</c> 开头调一次即可，免记 key 名。用户自定义参数直接 <c>GetParameter("MyParam")</c>。
/// <para/>
/// <b>参数定义</b>：UI 据静态 <c>ParameterDefinitions</c> 渲染输入框（静态约定，免实例化 QCAlgorithm）——
/// 在子类声明 <c>public static IReadOnlyList&lt;StrategyParameterDef&gt; ParameterDefinitions =&gt; new[]{ ... }</c>。
/// </remarks>
public abstract class LeanAlgorithmBase : QCAlgorithm
{
    /// <summary>订阅本地 CtpBar 数据（回测读预取 CSV / 实盘读 CTP tick 聚合）。等价 <c>AddData&lt;CtpBar&gt;(ticker, resolution)</c>。</summary>
    public Symbol AddCtpData(string ticker, Resolution resolution = Resolution.Daily)
        => AddData<CtpBar>(ticker, resolution).Symbol;

    /// <summary>从 Lean Config parameters 读 start/end/cash 并设置（在 Initialize 开头调一次）。</summary>
    protected void ConfigureFromConfig()
    {
        if (GetParameter("cash") is { Length: > 0 } c && decimal.TryParse(c, out var cash))
            SetCash(cash);
        else
            SetCash(100_000m);

        if (GetParameter("start") is { Length: > 0 } startStr && DateTime.TryParse(startStr, out var start))
            SetStartDate(start);
        else
            SetStartDate(2024, 1, 1);

        if (GetParameter("end") is { Length: > 0 } endStr && DateTime.TryParse(endStr, out var end))
            SetEndDate(end);
        else
            SetEndDate(DateTime.Today);
    }
}
