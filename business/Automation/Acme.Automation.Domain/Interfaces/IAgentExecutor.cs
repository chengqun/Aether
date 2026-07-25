namespace Acme.Automation.Domain.Interfaces;

public interface IAgentExecutor
{
    Task<string> ExecuteAsync(string agentId, string userMessage, CancellationToken cancellationToken = default);
}