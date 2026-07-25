using System.Collections.Generic;
using System.Linq;
using Acme.Trading.Domain;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;

namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// 回测与实盘共用的交易宿主（唯一 QCAlgorithm 子类，Lean backtest=live 承诺的落地）。
/// 数据链路两端一致——均走本地自定义 <see cref="CtpBar"/>：
/// 回测 CtpBar.Reader 读预取 CSV（IAnalysisManager cache-first），实盘 CtpBrokerage 把 CTP tick 聚合成 CtpBar 推 aggregator。
/// 策略走共享 <see cref="StrategyRegistry"/>/<see cref="IStrategy"/>——同一策略逻辑两边通用。
/// 下单代码也通用：回测 BacktestingBrokerage 模拟成交，实盘 CtpBrokerage（P1 拦截不下单，P2 真下单）。
/// 模式差异仅 Initialize：实盘（LiveMode）跳过 SetStartDate/SetEndDate/SetWarmUp（无历史回填），并检查 StopRequested→Quit。
/// </summary>
public class TradingHostAlgorithm : QCAlgorithm
{
    private readonly Dictionary<Symbol, IStrategy> _strategies = new();
    private readonly Dictionary<Symbol, bool> _invested = new();
    private decimal _targetPercent = 1.0m;
    private int _barSeen;   // 节流打 bar 样本（实盘确认 CtpBar 到达算法；回测 LogSink 为 null 自动 no-op）

    public override void Initialize()
    {
        SetCash(GetParameter("cash") is { Length: > 0 } c && decimal.TryParse(c, out var cash) ? cash : 100_000m);

        // 回测需要日期范围 + warmup；实盘从"现在"起、无历史回填（CTP 无历史 REST），跳过
        if (!LiveMode)
        {
            if (GetParameter("start") is { Length: > 0 } startStr && DateTime.TryParse(startStr, out var start)) SetStartDate(start);
            else SetStartDate(2024, 1, 1);
            if (GetParameter("end") is { Length: > 0 } endStr && DateTime.TryParse(endStr, out var end)) SetEndDate(end);
            else SetEndDate(2024, 12, 31);
        }

        var res = ParseResolution(GetParameter("resolution") ?? (LiveMode ? "Minute" : "Daily"));
        // instruments（逗号分隔，实盘多合约）；回测传 symbol 单标的，兜底取 symbol
        var instruments = (GetParameter("instruments") ?? GetParameter("symbol") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var strategyName = GetParameter("strategy") is { Length: > 0 } sn ? sn : "SmaCross";
        var parameters = BuildParameters();

        foreach (var ticker in instruments)
        {
            var symbol = AddData<CtpBar>(ticker, res).Symbol;
            var strategy = StrategyRegistry.Create(strategyName, parameters)
                ?? throw new InvalidOperationException($"未注册的策略: {strategyName}");
            _strategies[symbol] = strategy;
            _invested[symbol] = false;
            TradingGateway.Log($"[Algo] 订阅 {symbol.Value} strategy={strategyName} res={res} live={LiveMode}");
        }

        _targetPercent = _strategies.Count > 0 ? 1.0m / _strategies.Count : 1.0m;

        if (!LiveMode)
        {
            var warmup = _strategies.Values.Select(s => s.WarmUpBars).DefaultIfEmpty(0).Max();
            if (warmup > 0) SetWarmUp(warmup);
        }
        if (_strategies.Count == 0) TradingGateway.Log("[Algo] 警告：未订阅任何合约（instruments/symbol 参数为空）");
    }

    public override void OnData(Slice slice)
    {
        // 实盘停止：算法 OnData 检测 StopRequested 后 Quit（回测 LiveMode=false 不检查）
        if (LiveMode && TradingGateway.StopRequested) { Quit("用户停止"); return; }

        var bars = slice.Get<CtpBar>();
        TradingGateway.Log($"[OnData] Bars count: {bars.Count}, IsWarmingUp: {IsWarmingUp}, Strategies count: {_strategies.Count}");

        if (IsWarmingUp) return;

        foreach (var (symbol, strategy) in _strategies)
        {
            if (!bars.TryGetValue(symbol, out var bar))
            {
                TradingGateway.Log($"[OnData] Warning: No bar found for symbol {symbol.Value}");
                continue;
            }

            var n = ++_barSeen;
            if (n <= 5 || n % 200 == 0)
                TradingGateway.Log($"[Bar] {symbol.Value} o={bar.Open} h={bar.High} l={bar.Low} c={bar.Close} v={bar.Volume} {bar.Time:yyyy-MM-dd HH:mm} (#{n})");

            var marketBar = MarketBarMapper.FromCtpBar(bar);
            var signal = strategy.EvaluateSignal(marketBar);

            TradingGateway.Log($"[Signal] {symbol.Value} Signal={signal}, CurrentInvested={_invested[symbol]}, BarCount={n}");

            switch (signal)
            {
                case SignalDirection.Long when !_invested[symbol]:
                    SetHoldings(symbol, _targetPercent);
                    _invested[symbol] = true;
                    TradingGateway.Log($"[Signal] {symbol.Value} 做多 @ {bar.Close} ({bar.Time:yyyy-MM-dd HH:mm})");
                    break;
                case SignalDirection.Flat when _invested[symbol]:
                    Liquidate(symbol);
                    _invested[symbol] = false;
                    TradingGateway.Log($"[Signal] {symbol.Value} 平仓 @ {bar.Close} ({bar.Time:yyyy-MM-dd HH:mm})");
                    break;
                case SignalDirection.Long when _invested[symbol]:
                    TradingGateway.Log($"[Signal] {symbol.Value} 已持仓，忽略做多信号");
                    break;
                case SignalDirection.Flat when !_invested[symbol]:
                    TradingGateway.Log($"[Signal] {symbol.Value} 未持仓，忽略平仓信号");
                    break;
                case SignalDirection.Hold:
                    TradingGateway.Log($"[Signal] {symbol.Value} 持有不动");
                    break;
            }
        }
    }

    private StrategyParameters BuildParameters()
    {
        var dict = new Dictionary<string, string>();
        foreach (var kv in GetParameters()) dict[kv.Key] = kv.Value;
        return new StrategyParameters(dict);
    }

    private static Resolution ParseResolution(string r) => r?.ToLowerInvariant() switch
    {
        "minute" => Resolution.Minute,
        "second" => Resolution.Second,
        "hour" => Resolution.Hour,
        "tick" => Resolution.Tick,
        _ => Resolution.Daily,
    };
}
