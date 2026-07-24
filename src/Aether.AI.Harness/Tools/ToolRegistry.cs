using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Tools;

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

    /// <summary>Registers a tool, keyed by <see cref="AIFunction.Name"/>.</summary>
    public ToolRegistry Register(AIFunction tool)
    {
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        _tools[tool.Name] = tool;
        return this;
    }

    /// <summary>
    /// Creates a tool from a delegate and registers it. The delegate's method
    /// (and parameters) should be annotated with <c>[Description]</c> so the
    /// model understands how to call it.
    /// </summary>
    public ToolRegistry RegisterFunction(Delegate method, string? name = null)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        var tool = AIFunctionFactory.Create(method, name);
        return Register(tool);
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
