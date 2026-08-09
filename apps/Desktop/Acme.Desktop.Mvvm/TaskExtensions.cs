using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>fire-and-forget Task 扩展：await + 吞异常，防 UnobservedTaskException 静默吞错误。</summary>
public static class TaskExtensions
{
    /// <summary>安全触发一个不需等待的 Task（异常吞掉不崩进程）。</summary>
    public static async void SafeFireAndForget(this Task task)
    {
        try { await task; }
        catch { /* fire-and-forget：异常忽略，不崩进程 */ }
    }
}
