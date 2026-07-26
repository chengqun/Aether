using Acme.Agents.Shell;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <see cref="IUiDispatcher"/> 的 Avalonia 实现，转发到 UI 线程。
/// 使 UI 无关的 <c>Acme.Agents.Shell</c> ViewModel 不直接依赖 Avalonia。
/// </summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public Task InvokeAsync(Action action)
        => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
