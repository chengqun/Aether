using System.Collections.Concurrent;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Tools;

/// <summary>工具注册表贡献者接口——每个适配器通过此接口贡献工具元数据 + 工厂。</summary>
public interface IToolRegistryContributor
{
    void Contribute(ToolRegistry registry);
}

public sealed class DelegateToolRegistryContributor : IToolRegistryContributor
{
    private readonly Action<ToolRegistry> _configure;
    public DelegateToolRegistryContributor(Action<ToolRegistry> configure) => _configure = configure;
    public void Contribute(ToolRegistry registry) => _configure(registry);
}

/// <summary>工具元数据（名称 + 描述 + 分类 + 审批等级），供 UI 选择展示。
/// 审批等级 <see cref="ToolApprovalLevel"/> 已上移到 Aether.AI.Core.Agents。</summary>
public sealed class ToolMeta
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public ToolApprovalLevel ApprovalLevel { get; }

    public ToolMeta(string name, string description, string category, ToolApprovalLevel approvalLevel = ToolApprovalLevel.Safe)
    {
        Name = name;
        Description = description;
        Category = category;
        ApprovalLevel = approvalLevel;
    }
}

/// <summary>
/// Registry of <see cref="AIFunction"/> tools, keyed by tool name. Tools are
/// registered with metadata (name/description/category) + a factory at startup.
/// The factory is only invoked when resolving tools for an agent — GetAllMetas()
/// returns metadata without instantiating any AIFunction, so UI tool selection
/// never fails due to missing DI dependencies.
/// </summary>
public sealed class ToolRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ToolEntry> _entries = new();
    private readonly ILogger? _logger;

    public ToolRegistry() : this(NullServiceProvider.Instance) { }

    public ToolRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider is NullServiceProvider
            ? null
            : (serviceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<ToolRegistry>();

        // 执行各 AddXxx 模块通过 ConfigureToolRegistry 注册的元数据 + 工厂
        if (serviceProvider is NullServiceProvider) return;
        foreach (var contributor in serviceProvider.GetServices<IToolRegistryContributor>())
        {
            try { contributor.Contribute(this); }
            catch { /* skip */ }
        }
    }

    /// <summary>
    /// Registers a tool's metadata + factory. The factory is only invoked when
    /// the tool is resolved for an agent — not at registration time.
    /// </summary>
    /// <param name="approvalLevel">审批等级（默认 Safe 自动放行）；危险工具由适配器显式声明 Confirm/Dangerous。</param>
    public ToolRegistry Register(string name, string description, string category,
        Func<IServiceProvider, AIFunction> factory, ToolApprovalLevel approvalLevel = ToolApprovalLevel.Safe)
    {
        _entries[name] = new ToolEntry(name, description, category, factory, approvalLevel);
        return this;
    }

    /// <summary>
    /// 注册一个**已实例化**的工具（如 MCP server 解析出的 <see cref="AIFunction"/>）。
    /// 用 <see cref="ToolApprovalLevel.Dangerous"/> 默认等级（MCP 工具来源未知，强制审批更安全）。
    /// 名称冲突时跳过（不覆盖已注册的原生工具 / 先注册的 MCP 工具）。
    /// </summary>
    public bool RegisterMcpTool(AIFunction tool, string category = "MCP",
        ToolApprovalLevel approvalLevel = ToolApprovalLevel.Dangerous)
    {
        if (tool is null || string.IsNullOrEmpty(tool.Name)) return false;
        return _entries.TryAdd(tool.Name, new ToolEntry(tool.Name, tool.Description ?? "", category, _ => tool, approvalLevel));
    }

    /// <summary>
    /// 同步 MCP 工具集：移除「已注册为 MCP 类、但不在 <paramref name="currentMcpToolNames"/>」的条目。
    /// MCP server 重连后工具集可能变化（删工具/改名/换 server）——旧的 CLI 工具会残留指向已 dispose 的
    /// McpClient，不清理则 <see cref="ResolveAll"/> 时工厂仍返回失效 AIFunction、运行时调用报错。
    /// </summary>
    public void SyncMcpTools(IEnumerable<string> currentMcpToolNames)
    {
        var keep = new HashSet<string>(currentMcpToolNames, StringComparer.Ordinal);
        foreach (var name in _entries.Where(kv => kv.Value.Category == "MCP" && !keep.Contains(kv.Key))
                                      .Select(kv => kv.Key).ToList())
        {
            if (_entries.TryRemove(name, out _))
                _logger?.LogInformation("已移除失效 MCP 工具 {Tool}。", name);
        }
    }

    /// <summary>获取所有工具的元数据（名称 + 描述 + 分类 + 审批等级），不实例化 AIFunction。</summary>
    public IReadOnlyList<ToolMeta> GetAllMetas()
        => _entries.Values
            .Select(t => new ToolMeta(t.Name, t.Description, t.Category, t.ApprovalLevel))
            .OrderBy(t => t.Category).ThenBy(t => t.Name)
            .ToList();

    /// <summary>查询工具的审批等级；未注册返回 Safe。</summary>
    public ToolApprovalLevel LevelOf(string name)
        => _entries.TryGetValue(name, out var entry) ? entry.ApprovalLevel : ToolApprovalLevel.Safe;

    /// <summary>Resolves tools by name, invoking factories lazily.</summary>
    public IReadOnlyList<AIFunction> ResolveAll(IEnumerable<string> names)
    {
        var resolved = new List<AIFunction>();
        foreach (var name in names)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                try { resolved.Add(entry.Factory(_serviceProvider)); }
                catch (Exception ex)
                {
                    // 静默跳过但仍记录——agent 勾选了却拿不到该工具，通常是 MCP server 未连接/连接失败所致，
                    // 日志可追溯「哪个工具、为何缺」。
                    _logger?.LogWarning(ex, "工具 {Tool} 实例化失败，已跳过。", name);
                }
            }
            else
            {
                // 名字未注册：大概率是 MCP 工具未就绪（server 未连上），或 agent 持久化里残留了已删除的工具名。
                _logger?.LogDebug("工具 {Tool} 未注册，已跳过（可能是 MCP server 未连接）。", name);
            }
        }
        return resolved;
    }

    /// <summary>Whether a tool with the given name is registered.</summary>
    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>All registered tool names.</summary>
    public IReadOnlyCollection<string> AllNames => _entries.Keys.ToList();

    private sealed class ToolEntry
    {
        public string Name { get; }
        public string Description { get; }
        public string Category { get; }
        public Func<IServiceProvider, AIFunction> Factory { get; }
        public ToolApprovalLevel ApprovalLevel { get; }

        public ToolEntry(string name, string description, string category,
            Func<IServiceProvider, AIFunction> factory, ToolApprovalLevel approvalLevel)
        {
            Name = name;
            Description = description;
            Category = category;
            Factory = factory;
            ApprovalLevel = approvalLevel;
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
