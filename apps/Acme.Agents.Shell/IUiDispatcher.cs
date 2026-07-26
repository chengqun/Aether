namespace Acme.Agents.Shell;

/// <summary>
/// UI 线程调度抽象，使本库的 ViewModel 不依赖任何具体 UI 框架
/// （Avalonia / MAUI / WPF 各自提供实现）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>在 UI 线程执行并等待完成。</summary>
    Task InvokeAsync(Action action);
}
