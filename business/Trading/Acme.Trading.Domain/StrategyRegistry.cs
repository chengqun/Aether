namespace Acme.Trading.Domain;

/// <summary>
/// 策略注册表（静态，纯领域无 Lean 依赖）。Lean 用反射无参 ctor 创建 algorithm，host 拿不到 DI，故用静态注册表。
/// AddTradingRuntimeLean 启动时 Register 内置策略，TradingHostAlgorithm 按 strategy-name 解析。
/// 回测与实盘共用同一注册表 → 同一策略逻辑。未来动态/AI 策略可同样 Register。
/// </summary>
public static class StrategyRegistry
{
    private static readonly Dictionary<string, Func<IStrategy>> _factories = new();

    public static void Register(string name, Func<IStrategy> factory)
        => _factories[name] = factory;

    /// <summary>注销一个已注册策略（动态脚本策略重加载前清理旧的，避免选择器残留失效策略）。</summary>
    public static bool Unregister(string name)
        => _factories.Remove(name);

    public static IStrategy? Create(string name, StrategyParameters parameters)
    {
        if (!_factories.TryGetValue(name, out var factory)) return null;
        var strategy = factory();
        strategy.Initialize(parameters);
        return strategy;
    }

    /// <summary>已注册策略名（供 UI 下拉动态取值；新注册的策略自动出现）。</summary>
    public static IReadOnlyList<string> GetRegisteredNames() => _factories.Keys.ToArray();

    /// <summary>获取策略的参数定义（UI 据此动态渲染参数输入框）。策略不存在或无参数返回空列表。</summary>
    public static IReadOnlyList<StrategyParameterDef> GetParameterDefinitions(string name)
    {
        if (!_factories.TryGetValue(name, out var factory)) return Array.Empty<StrategyParameterDef>();
        return factory().ParameterDefinitions;
    }
}
