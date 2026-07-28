namespace Acme.Automation.Domain.Interfaces;

public interface IAutomationScheduler
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>立即执行指定任务（不论是否到期），用于「立即运行」按钮。</summary>
    Task RunNowAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>任务执行完成（成功或失败）时触发，参数为任务 Id。UI 订阅以刷新列表。</summary>
    event Action<int>? TaskCompleted;
}
