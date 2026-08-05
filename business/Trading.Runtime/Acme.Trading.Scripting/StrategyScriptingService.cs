using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Trading.Domain;
using Acme.Trading.Runtime.Domain;
using Microsoft.Extensions.Logging;

namespace Acme.Trading.Scripting;

/// <summary>策略脚本加载服务实现。扫描策略笔记 → 编译 → Register。
/// 通过 IStrategyNoteSource 端口读笔记（不直接依赖 Notes.Application）；host 订阅 NotesChanged 后调 Reload()。</summary>
public sealed class StrategyScriptingService : IStrategyScriptingService
{
    private readonly IStrategyNoteSource _source;
    private readonly ILogger<StrategyScriptingService> _logger;
    private readonly Dictionary<string, string> _loadErrors = new();
    private readonly HashSet<string> _registeredNames = new(); // 本服务动态注册的策略名，重加载时 unregister
    private readonly object _lock = new();
    private int _loading; // 防并发重入
    private CancellationTokenSource? _debounceCts;

    public event Action? StrategiesLoaded;

    public IReadOnlyDictionary<string, string> LoadErrors
    {
        get { lock (_lock) return new Dictionary<string, string>(_loadErrors); }
    }

    public StrategyScriptingService(IStrategyNoteSource source, ILogger<StrategyScriptingService> logger)
    {
        _source = source;
        _logger = logger;
    }

    /// <summary>host 订阅 NotesChanged 后调用此方法触发防抖重加载（500ms）。</summary>
    public void Reload()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;
        _ = Task.Delay(500, ct).ContinueWith(_ => LoadAsync(ct), TaskScheduler.Default);
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 防并发重入
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0) return;
        try
        {
            var all = await _source.GetAllAsync(cancellationToken);
            var strategyNotes = all.Where(IsStrategyNote).ToList();

            // 先 unregister 上次动态注册的（清理已删除/改名的策略，避免选择器残留失效策略）
            foreach (var name in _registeredNames) StrategyRegistry.Unregister(name);
            _registeredNames.Clear();
            lock (_lock) _loadErrors.Clear();

            foreach (var note in strategyNotes)
            {
                if (string.IsNullOrWhiteSpace(note.Content))
                {
                    lock (_lock) _loadErrors[note.Title] = "策略源码为空";
                    continue;
                }
                if (StrategyScriptCompiler.TryCompile(note.Content!, out var instance, out var err) && instance is not null)
                {
                    var type = instance.GetType();
                    var name = note.Title;
                    StrategyRegistry.Register(name, () => (IStrategy)Activator.CreateInstance(type)!);
                    _registeredNames.Add(name);
                    _logger.LogInformation("已加载策略脚本：{Name}", name);
                }
                else
                {
                    lock (_lock) _loadErrors[note.Title] = err ?? "未知编译错误";
                    _logger.LogWarning("策略脚本编译失败 {Name}：{Err}", note.Title, err);
                }
            }
            StrategiesLoaded?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _loading, 0);
        }
    }

    /// <summary>判断是否策略笔记：Tags 含 "strategy"（逗号分隔，忽略大小写）。</summary>
    private static bool IsStrategyNote(StrategyNote n)
    {
        if (string.IsNullOrEmpty(n.Tags)) return false;
        var tags = n.Tags!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Contains("strategy", StringComparer.OrdinalIgnoreCase);
    }
}
