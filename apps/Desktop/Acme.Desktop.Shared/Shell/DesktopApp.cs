using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 共享层访问租户 DI 容器的静态入口。各租户 App 在构建 ServiceProvider 后赋值 <see cref="Services"/>，
/// 供 shared 代码（如 MainWindow/NotesView 的布局持久化）解析 <c>IPreferencesStore</c> 等服务。
/// </summary>
public static class DesktopApp
{
    public static IServiceProvider Services { get; set; } = null!;

    /// <summary>
    /// 容错解析服务：dock Quit（右键 dock→退出 / Cmd+Q）走 <c>ShutdownRequested</c>，其
    /// <c>await DisposeAsync()</c> 先于窗口关闭释放了 DI 容器——此后 <c>OnClosing</c> 链里的
    /// <c>GetService</c> 会抛 <c>ObjectDisposedException</c>，未经捕获成为未处理异常 → SIGABRT 崩溃
    ///（见 <c>~/Desktop/Aether-crash.log</c>：栈顶 WorkspaceView.OnHostClosing）。而桌宠右键退出路径
    /// 先 <c>Close()</c> 主窗（容器仍活）再 ShutdownRequested 释放容器，故不崩——dock Quit 是次序倒置。
    /// 关闭期 best-effort 持久化回调（WorkspaceView.OnHostClosing / KO.PersistLayout / Docs.PersistLayout /
    /// WorkbenchWindow.PersistWindowSize / Toast DetachHost）改走本方法：容器已释放则返回 null 静默跳过，
    /// 不抛——丢写仅是下次用上次落盘的布局。
    /// </summary>
    public static T? TryGetService<T>() where T : class
    {
        try { return Services?.GetService<T>(); }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>
    /// 解析「当前应作为模态窗口 owner 的可见窗口」。由 WorkbenchWindow 在桌宠进入/退出时更新：
    /// 主窗可见 → 返回主窗；桌宠模式（主窗已 Hide）→ 返回桌宠。审批窗/设置窗等模态窗经此拿 owner，
    /// 避免「桌宠模式下主窗已 Hide、ShowDialog(主窗) 抛 Cannot show window with non-visible owner」。
    /// null=未设置（设计期/宿主未来），调用方退回 <c>desktop.MainWindow</c> 或无 owner 的 <c>Show()</c>。
    /// </summary>
    public static Func<Window?>? ModalOwnerResolver { get; set; }

    /// <summary>
    /// 用「可见 owner」模态显示窗口，返回其关闭完成的 Task（结果由调用方从 VM 读）。解决桌宠模式下主窗
    /// 已 Hide → <c>ShowDialog(主窗)</c> 抛 "Cannot show window with non-visible owner" 的问题：
    /// 有可见 owner（主窗或桌宠）走 <c>ShowDialog(owner)</c> 模态；否则退回无 owner 的 <c>Show()</c>，
    /// 经 <c>Closed</c> 完成 Task。审批窗/ask_user 窗等 agent 运行中弹出的模态窗都走此 helper。
    /// </summary>
    public static async Task ShowModalWithVisibleOwnerAsync(Window window)
    {
        var owner = ModalOwnerResolver?.Invoke();
        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
    }
}

