using System.Diagnostics;
using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>fire-and-forget Task 扩展：await + 记异常到 trace 通道，防 UnobservedTaskException 静默吞错误。</summary>
public static class TaskExtensions
{
    /// <summary>
    /// 非致命错误观察口：SafeFireAndForget 捕获异常时触发（81 处调用点的单一咽喉）。
    /// 宿主层（Shared 的 CrashRecorder）订阅它接崩溃遥测——Mvvm 层保持零 recorder 依赖（层化方向：Shared→Mvvm）。
    /// handler 异常绝不外逃（调用列表拷贝 + per-handler try/catch）。
    /// </summary>
    public static event Action<Exception>? UnobservedError;

    /// <summary>安全触发一个不需等待的 Task（异常记到 trace 不崩进程）。
    /// 异常写到 <see cref="Trace"/>（开发期调试器输出窗口 / <c>dotnet run</c> 控制台可见），
    /// 避免裸 <c>_ = task</c> 的异常成为不可观测的 UnobservedTaskException。</summary>
    public static async void SafeFireAndForget(this Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SafeFireAndForget] {ex}");
            RaiseUnobservedError(ex);
        }
    }

    private static void RaiseUnobservedError(Exception ex)
    {
        var handlers = UnobservedError;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Exception>>())
        {
            try { handler(ex); }
            catch { /* handler 绝不外逃 */ }
        }
    }
}
