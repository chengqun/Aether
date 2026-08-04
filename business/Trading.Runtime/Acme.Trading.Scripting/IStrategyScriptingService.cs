using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Trading.Scripting;

/// <summary>策略脚本加载服务：扫描策略笔记 → Roslyn 编译 → 注册到 StrategyRegistry。</summary>
public interface IStrategyScriptingService
{
    /// <summary>扫描策略笔记并编译注册。策略笔记约定：Tags 含 "strategy"，Content 为 IStrategy 实现类 C# 源码。</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>编译失败的策略名→错误（供 UI/log 查看；成功的策略不在内）。</summary>
    IReadOnlyDictionary<string, string> LoadErrors { get; }

    /// <summary>策略脚本加载完成（StrategyRegistry 已更新）时触发，供 UI 刷新策略选择器。</summary>
    event Action? StrategiesLoaded;
}
