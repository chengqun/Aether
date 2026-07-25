namespace Acme.Automation.Application.Interfaces;

public interface IAgentExecutor
{
    Task<string> ExecuteAsync(string agentId, string userMessage, CancellationToken cancellationToken = default);
}