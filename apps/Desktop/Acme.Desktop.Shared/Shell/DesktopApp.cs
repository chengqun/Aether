using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 共享层访问租户 DI 容器的静态入口。各租户 App 在构建 ServiceProvider 后赋值 <see cref="Services"/>，
/// 供 shared 代码（如 MainWindow/NotesView 的布局持久化）解析 <c>IPreferencesStore</c> 等服务。
/// </summary>
public static class DesktopApp
{
    public static IServiceProvider Services { get; set; } = null!;

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

