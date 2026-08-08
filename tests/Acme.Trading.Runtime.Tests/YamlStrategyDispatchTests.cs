using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Acme.Trading.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Trading.Runtime.Tests;

/// <summary>
/// YAML 策略解析 + Code/Yaml 分发测试。
/// 验证：StrategyProgramYaml 解析结构正确；StrategyScriptingService 按 Kind 分发（Yaml→YamlStrategy 注册，
/// Code 路径不动）；坏 YAML 进 LoadErrors。
/// </summary>
public class YamlStrategyDispatchTests
{
    private const string SmaCrossYaml = @"params:
  - { key: FastPeriod, label: 快线周期, default: 5 }
  - { key: SlowPeriod, label: 慢线周期, default: 20 }
indicators:
  fast: { type: sma, period: ""{FastPeriod}"" }
  slow: { type: sma, period: ""{SlowPeriod}"" }
rules:
  - signal: Long
    when: { cross_above: [fast, slow] }
  - signal: Flat
    when: { cross_below: [fast, slow] }
default: Hold
";

    [Fact]
    public void TryDeserialize_parses_structure_and_templating()
    {
        Assert.True(StrategyProgramYaml.TryDeserialize(SmaCrossYaml, out var prog));
        Assert.NotNull(prog);
        Assert.Equal(2, prog!.Params.Count);
        Assert.Equal("FastPeriod", prog.Params[0].Key);

        Assert.Equal(2, prog.Indicators.Count);
        Assert.True(prog.Indicators.ContainsKey("fast"));
        Assert.Equal("sma", prog.Indicators["fast"].Type);
        Assert.Equal("{FastPeriod}", prog.Indicators["fast"].Args["period"]); // 模板原样保留，运行时替换

        Assert.Equal(2, prog.Rules.Count);
        Assert.Equal(SignalDirection.Long, prog.Rules[0].Signal);
        Assert.Equal("cross_above", prog.Rules[0].When.Op);
        Assert.Equal(SignalDirection.Hold, prog.Default);
    }

    [Fact]
    public void TryDeserialize_rejects_malformed()
    {
        Assert.False(StrategyProgramYaml.TryDeserialize("not: valid: yaml: [", out _));
        Assert.False(StrategyProgramYaml.TryDeserialize("", out _));
        Assert.False(StrategyProgramYaml.TryDeserialize(null, out _));
    }

    [Fact]
    public async Task LoadAsync_registers_yaml_strategy_and_surfaces_errors()
    {
        // 用唯一标题避免与其它测试/全局注册表冲突
        var title = "TestYamlSmaCross_" + Guid.NewGuid().ToString("N")[..8];
        var badTitle = "TestYamlBad_" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeStrategySource(new List<StrategyNote>
        {
            new("y1", title, SmaCrossYaml, StrategyKind.Yaml),
            new("y2", badTitle, "  - not: valid: yaml: [", StrategyKind.Yaml),
        });
        var svc = new StrategyScriptingService(source, NullLogger<StrategyScriptingService>.Instance);

        try
        {
            await svc.LoadAsync();

            Assert.Contains(title, StrategyRegistry.GetRegisteredNames());
            // 参数定义从 YAML params 流出
            var defs = StrategyRegistry.GetParameterDefinitions(title);
            Assert.Contains(defs, d => d.Key == "FastPeriod");
            Assert.Contains(defs, d => d.Key == "SlowPeriod");

            // 坏 YAML 不注册，进 LoadErrors
            Assert.DoesNotContain(badTitle, StrategyRegistry.GetRegisteredNames());
            Assert.True(svc.LoadErrors.ContainsKey(badTitle));
        }
        finally
        {
            StrategyRegistry.Unregister(title);
            StrategyRegistry.Unregister(badTitle);
        }
    }

    [Fact]
    public async Task LoadAsync_registered_yaml_strategy_runs_end_to_end()
    {
        var title = "TestYamlRun_" + Guid.NewGuid().ToString("N")[..8];
        // fast=SMA(2) slow=SMA(3)，cross_above→Long, cross_below→Flat
        var yaml = @"params: []
indicators:
  fast: { type: sma, period: 2 }
  slow: { type: sma, period: 3 }
rules:
  - signal: Long
    when: { cross_above: [fast, slow] }
  - signal: Flat
    when: { cross_below: [fast, slow] }
default: Hold
";
        var source = new FakeStrategySource(new List<StrategyNote>
        {
            new("r1", title, yaml, StrategyKind.Yaml),
        });
        var svc = new StrategyScriptingService(source, NullLogger<StrategyScriptingService>.Instance);

        try
        {
            await svc.LoadAsync();
            var strategy = StrategyRegistry.Create(title, new StrategyParameters(null));
            Assert.NotNull(strategy);
            Assert.Equal(3, strategy!.WarmUpBars);

            // closes 10,10,10,20,20,5 → Hold,Hold,Hold,Long,Hold,Flat（与 Domain 测试同场景）
            var closes = new decimal[] { 10, 10, 10, 20, 20, 5 };
            var expected = new[] { SignalDirection.Hold, SignalDirection.Hold, SignalDirection.Hold,
                                   SignalDirection.Long, SignalDirection.Hold, SignalDirection.Flat };
            for (int i = 0; i < closes.Length; i++)
                Assert.Equal(expected[i], strategy.EvaluateSignal(MkBar(closes[i])));
        }
        finally
        {
            StrategyRegistry.Unregister(title);
        }
    }

    private static MarketBar MkBar(decimal close) => new(DateTime.MinValue, close, close, close, close, 0);

    private sealed class FakeStrategySource : IStrategyNoteSource
    {
        private readonly IReadOnlyList<StrategyNote> _notes;
        public FakeStrategySource(IReadOnlyList<StrategyNote> notes) => _notes = notes;
        public Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_notes);
    }
}
