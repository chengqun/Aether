using Aether.AI.Core.Agents;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Fluent builder for <see cref="AgentDefinition"/>. Produces a plain data
/// model; the <see cref="AgentFactory"/> is responsible for turning the
/// resulting definition into a live agent.
/// </summary>
public sealed class AgentBuilder
{
    private readonly string _id;
    private string _name = string.Empty;
    private string _instructions = string.Empty;
    private string _modelId = string.Empty;
    private double? _temperature;
    private readonly List<string> _tools = new();

    public AgentBuilder(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Agent id is required.", nameof(id));
        _id = id;
    }

    public AgentBuilder Name(string name) { _name = name; return this; }
    public AgentBuilder Instructions(string instructions) { _instructions = instructions; return this; }
    public AgentBuilder ModelId(string modelId) { _modelId = modelId; return this; }
    public AgentBuilder Temperature(double temperature) { _temperature = temperature; return this; }
    public AgentBuilder WithTool(string toolName) { _tools.Add(toolName); return this; }
    public AgentBuilder WithTools(IEnumerable<string> toolNames) { _tools.AddRange(toolNames); return this; }

    public AgentDefinition Build() => new()
    {
        Id = _id,
        Name = string.IsNullOrWhiteSpace(_name) ? _id : _name,
        Instructions = _instructions,
        ModelId = _modelId,
        Temperature = _temperature,
        Tools = _tools
    };
}
