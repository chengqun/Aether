using System.Collections.Generic;
using System.Linq;

namespace Acme.Trading.Domain.Strategies;

/// <summary>
/// SMA 双线交叉策略（纯领域，不依赖 QuantConnect）。
/// 维护 close 的滚动窗口，算 fast/slow 两条简单均线：fast&gt;slow 返 Long，fast&lt;slow 返 Flat，未就绪返 Hold。
/// 可在 Domain.Tests 脱离 Lean 单测。回测/实盘共用。
/// </summary>
public sealed class SmaCrossStrategy : IStrategy
{
    private readonly Queue<decimal> _window = new();
    private int _fastPeriod = 10;
    private int _slowPeriod = 30;

    public int WarmUpBars => _slowPeriod + 1;

    public void Initialize(StrategyParameters parameters)
    {
        _fastPeriod = parameters.Get("fast", 10);
        _slowPeriod = parameters.Get("slow", 30);
        if (_fastPeriod >= _slowPeriod)
            _fastPeriod = _slowPeriod - 1; // fast 必须小于 slow，兜底
    }

    public SignalDirection EvaluateSignal(MarketBar bar)
    {
        _window.Enqueue(bar.Close);
        if (_window.Count > _slowPeriod) _window.Dequeue();
        if (_window.Count < _slowPeriod) return SignalDirection.Hold;

        var snapshot = _window.ToList();
        var fast = Average(snapshot, snapshot.Count - _fastPeriod, _fastPeriod);
        var slow = Average(snapshot, 0, _slowPeriod);

        return fast > slow ? SignalDirection.Long : SignalDirection.Flat;
    }

    private static decimal Average(List<decimal> values, int start, int count)
    {
        decimal sum = 0;
        for (var i = start; i < start + count; i++) sum += values[i];
        return sum / count;
    }
}
