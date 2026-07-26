namespace Acme.Shell;

/// <summary>
/// UI 线程调度抽象，使本库的 ViewModel 不依赖任何具体 UI 框架
/// （Avalonia / MAUI / WPF 各自提供实现）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>在 UI 线程执行并等待完成。</summary>
    Task InvokeAsync(Action action);

    /// <summary>把动作投递到 UI 线程异步执行（fire-and-forget，不等待）。</summary>
    void Post(Action action);
}
