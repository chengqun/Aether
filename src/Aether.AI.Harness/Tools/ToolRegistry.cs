using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tools;

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
/// registered once at startup and resolved by name when building an agent from
/// an <see cref="Aether.AI.Core.Agents.AgentDefinition"/> (which lists tool
/// names, not tool instances).
/// <para>
/// This deliberately wraps MEAI's <c>AIFunction</c> rather than defining a
/// competing <c>ITool</c> abstraction — Microsoft Agent Framework already
/// provides the tool model.
/// </para>
/// </summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, AIFunction> _tools = new();
    private readonly ConcurrentDictionary<string, string> _categories = new();

    /// <summary>Registers a tool, keyed by <see cref="AIFunction.Name"/>.</summary>
    public ToolRegistry Register(AIFunction tool, string category = "未分类")
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        _tools[tool.Name] = tool;
        _categories[tool.Name] = category;
        return this;
    }

    /// <summary>设置某个工具的分类（用于已注册工具的分类补充）。</summary>
    public void SetCategory(string toolName, string category)
    {
        _categories[toolName] = category;
    }

    /// <summary>
    /// Creates a tool from a delegate and registers it. The delegate's method
    /// (and parameters) should be annotated with <c>[Description]</c> so the
    /// model understands how to call it.
    /// </summary>
    public ToolRegistry RegisterFunction(Delegate method, string? name = null, string category = "未分类")
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        var tool = AIFunctionFactory.Create(method, name);
        return Register(tool, category);
    }

    /// <summary>获取所有工具的元数据（名称 + 描述 + 分类），供 UI 选择展示。</summary>
    public IReadOnlyList<ToolMeta> GetAllMetas()
    {
        var result = new List<ToolMeta>();
        foreach (var kv in _tools)
        {
            var name = kv.Key;
            var tool = kv.Value;
            var desc = tool.Description ?? string.Empty;
            var cat = _categories.TryGetValue(name, out var c) ? c : "未分类";
            result.Add(new ToolMeta(name, desc, cat));
        }
        return result.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    /// <summary>Resolves a tool by name, or <c>null</c> if not registered.</summary>
    public AIFunction? Resolve(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>Resolves every tool whose name appears in <paramref name="names"/>.</summary>
    public IReadOnlyList<AIFunction> ResolveAll(IEnumerable<string> names)
    {
        var resolved = new List<AIFunction>();
        foreach (var name in names)
        {
            if (_tools.TryGetValue(name, out var tool))
                resolved.Add(tool);
        }
        return resolved;
    }

    /// <summary>All registered tools.</summary>
    public IReadOnlyCollection<AIFunction> All => _tools.Values.ToList();

    /// <summary>Removes a tool by name. Returns <c>true</c> if removed.</summary>
    public bool Remove(string name) => _tools.TryRemove(name, out _);

    /// <summary>Whether a tool with the given name is registered.</summary>
    public bool Contains(string name) => _tools.ContainsKey(name);
}
