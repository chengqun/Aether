using Acme.Trading.Domain;
using Acme.Trading.Domain.Strategies;

namespace Acme.Trading.Domain.Tests;

public class StrategyTests
{
    private static MarketBar Bar(DateTime t, decimal close) =>
        new(t, close, close, close, close, 0m);

    [Fact]
    public void SmaCross_returns_Hold_until_slow_window_filled()
    {
        var s = new SmaCrossStrategy();
        s.Initialize(new StrategyParameters(new Dictionary<string, string>
        {
            ["fast"] = "2",
            ["slow"] = "5",
        }));

        // WarmUpBars = slow+1 = 6; before that must be Hold.
        for (var i = 0; i < 4; i++)
            Assert.Equal(SignalDirection.Hold, s.EvaluateSignal(Bar(DateTime.Today.AddDays(i), 10 + i)));

        // After slow window (5 bars) filled, signals are Long/Flat based on fast vs slow.
        // With rising prices fast>slow → Long.
        var signal = s.EvaluateSignal(Bar(DateTime.Today.AddDays(4), 100));
        Assert.Equal(SignalDirection.Long, signal);
    }

    [Fact]
    public void SmaCross_falls_back_when_fast_not_less_than_slow()
    {
        var s = new SmaCrossStrategy();
        s.Initialize(new StrategyParameters(new Dictionary<string, string>
        {
            ["fast"] = "5",
            ["slow"] = "5",
        }));
        // fast>=slow → ctor forces fast=slow-1; WarmUpBars should be slow+1=6
        Assert.Equal(6, s.WarmUpBars);
    }

    [Fact]
    public void TestStrategy_signals_on_bar_direction()
    {
        var s = new TestStrategy();
        s.Initialize(new StrategyParameters(null));
        Assert.Equal(0, s.WarmUpBars);

        var up = new MarketBar(DateTime.Today, 1m, 2m, 0.5m, 1.5m, 0m);
        var down = new MarketBar(DateTime.Today, 2m, 2m, 0.5m, 0.8m, 0m);
        var flat = new MarketBar(DateTime.Today, 1m, 2m, 0.5m, 1m, 0m);

        Assert.Equal(SignalDirection.Long, s.EvaluateSignal(up));
        Assert.Equal(SignalDirection.Flat, s.EvaluateSignal(down));
        Assert.Equal(SignalDirection.Hold, s.EvaluateSignal(flat));
    }

    [Fact]
    public void StrategyRegistry_resolves_registered_strategy()
    {
        StrategyRegistry.Register("Test", () => new TestStrategy());
        var p = new StrategyParameters(null);
        var s = StrategyRegistry.Create("Test", p);
        Assert.NotNull(s);
        Assert.IsType<TestStrategy>(s);
    }

    [Fact]
    public void StrategyRegistry_returns_null_for_unknown()
    {
        Assert.Null(StrategyRegistry.Create("does-not-exist", new StrategyParameters(null)));
    }
}
