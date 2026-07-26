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

/// <summary>工具元数据（名称 + 描述 + 分类），供 UI 选择展示。</summary>
public sealed class ToolMeta
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }

    public ToolMeta(string name, string description, string category)
    {
        Name = name;
        Description = description;
        Category = category;
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
    public ToolRegistry Register(string name, string description, string category,
        Func<IServiceProvider, AIFunction> factory)
    {
        _entries[name] = new ToolEntry(name, description, category, factory);
        return this;
    }

    /// <summary>获取所有工具的元数据（名称 + 描述 + 分类），不实例化 AIFunction。</summary>
    public IReadOnlyList<ToolMeta> GetAllMetas()
        => _entries.Values
            .Select(t => new ToolMeta(t.Name, t.Description, t.Category))
            .OrderBy(t => t.Category).ThenBy(t => t.Name)
            .ToList();

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

        public ToolEntry(string name, string description, string category,
            Func<IServiceProvider, AIFunction> factory)
        {
            Name = name;
            Description = description;
            Category = category;
            Factory = factory;
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
