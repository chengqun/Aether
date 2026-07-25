namespace Acme.Trading.Domain.Strategies;

/// <summary>测试策略：close&gt;open 做多，close&lt;open 平仓，否则持有。纯领域无 Lean 依赖。</summary>
public sealed class TestStrategy : IStrategy
{
    public int WarmUpBars => 0;

    public void Initialize(StrategyParameters parameters)
    {
    }

    public SignalDirection EvaluateSignal(MarketBar bar)
    {
        if (bar.Close > bar.Open)
        {
            return SignalDirection.Long;
        }
        if (bar.Close < bar.Open)
        {
            return SignalDirection.Flat;
        }
        return SignalDirection.Hold;
    }
}
