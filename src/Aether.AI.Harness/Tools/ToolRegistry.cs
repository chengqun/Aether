using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

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

/// <summary>工具审批等级：Safe 自动放行；Confirm 需审批；Dangerous 需审批且 UI 强调（红）。
/// 由工具适配器在注册时显式声明；AgentFactory 在 RequireToolApproval 时按级包装非 Safe 工具。</summary>
public enum ToolApprovalLevel
{
    /// <summary>只读/无副作用：自动放行，不弹审批。</summary>
    Safe = 0,
    /// <summary>有副作用/可逆：RequireToolApproval 时弹审批。</summary>
    Confirm = 1,
    /// <summary>不可逆/高风险（下单/删除/执行命令）：RequireToolApproval 时弹审批并红色强调。</summary>
    Dangerous = 2,
}

/// <summary>工具元数据（名称 + 描述 + 分类 + 审批等级），供 UI 选择展示。</summary>
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

    public ToolRegistry() : this(NullServiceProvider.Instance) { }

    public ToolRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;

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
                catch { /* skip tools that fail to instantiate */ }
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
