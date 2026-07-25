using Acme.Automation.Domain.Interfaces;
using Aether.AI.Harness;

namespace Acme.Automation.Infrastructure;

public class HarnessAgentExecutor : IAgentExecutor
{
    private readonly HarnessRuntime _runtime;

    public HarnessAgentExecutor(HarnessRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task<string> ExecuteAsync(string agentId, string userMessage, CancellationToken cancellationToken = default)
    {
        if (!_runtime.HasAgent(agentId))
            throw new InvalidOperationException($"Agent '{agentId}' is not registered.");

        var session = _runtime.StartSession(agentId);
        return await _runtime.RunAsync(session.SessionId, userMessage, cancellationToken);
    }
}