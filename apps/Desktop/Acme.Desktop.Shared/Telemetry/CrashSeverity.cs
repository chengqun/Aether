namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// 崩溃记录严重级别（客户端镜像枚举；值须与服务端 <c>Aether.Server.Domain.Shared.Enums.CrashSeverity</c> 对齐）。
/// </summary>
public enum CrashSeverity
{
    /// <summary>致命：未处理异常 / 非干净关机（进程崩溃或被强杀）。</summary>
    Fatal = 0,

    /// <summary>非致命：SafeFireAndForget 捕获的后台异常 / 关窗路径被吞的异常等，进程未死。</summary>
    NonFatal = 1
}

/// <summary>
/// 崩溃捕获来源常量（存 crash_report.source，服务端同样按字符串过滤；均 ≤32 字符）。
/// </summary>
public static class CrashSource
{
    /// <summary>AppDomain.CurrentDomain.UnhandledException（进程级致命）。</summary>
    public const string UnhandledException = "UnhandledException";

    /// <summary>TaskScheduler.UnobservedTaskException（fire-and-forget 泄漏的任务异常）。</summary>
    public const string UnobservedTask = "UnobservedTask";

    /// <summary>TaskExtensions.SafeFireAndForget 捕获的后台异常（81 处调用点的单一咽喉）。</summary>
    public const string SafeFireAndForget = "SafeFireAndForget";

    /// <summary>App.OnFrameworkInitializationCompleted / ConfigureServices 启动期异常。</summary>
    public const string Startup = "Startup";

    /// <summary>退出路径被 try/catch 吞掉的异常（ShutdownRequested / PrepareForShutdown / PetWindow.OnClosed）。</summary>
    public const string Shutdown = "Shutdown";

    /// <summary>非干净关机墓碑：上次会话没走 MarkCleanShutdown 就没了（强杀/原生 SIGABRT——托管 handler 抓不到的唯一兜底）。</summary>
    public const string UncleanShutdown = "UncleanShutdown";
}
