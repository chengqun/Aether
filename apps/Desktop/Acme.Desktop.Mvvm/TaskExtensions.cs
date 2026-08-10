using System.Diagnostics;
using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>fire-and-forget Task 扩展：await + 记异常到 trace 通道，防 UnobservedTaskException 静默吞错误。</summary>
public static class TaskExtensions
{
    /// <summary>安全触发一个不需等待的 Task（异常记到 trace 不崩进程）。
    /// 异常写到 <see cref="Trace"/>（开发期调试器输出窗口 / <c>dotnet run</c> 控制台可见），
    /// 避免裸 <c>_ = task</c> 的异常成为不可观测的 UnobservedTaskException。</summary>
    public static async void SafeFireAndForget(this Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SafeFireAndForget] {ex}");
        }
    }
}
