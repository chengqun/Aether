namespace Acme.Automation.Domain.Interfaces;

public interface IAutomationScheduler
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}