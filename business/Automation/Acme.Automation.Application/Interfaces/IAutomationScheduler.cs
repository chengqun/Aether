namespace Acme.Automation.Application.Interfaces;

public interface IAutomationScheduler
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}