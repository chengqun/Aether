using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// 崩溃遥测录制器（静态，Program.cs Main 首行初始化——DI 之前就要能接住异常）。
/// <para><b>全路径防御</b>：每个公开方法 try/catch 吞——遥测自身失败绝不阻断启动/崩溃路径（否则
/// "记录崩溃的代码把进程搞崩" 就是新的崩溃源）。</para>
/// <para>捕获面：AppDomain 未处理异常（Fatal）/ 未观察 Task 异常（NonFatal + SetObserved）/
/// <see cref="TaskExtensions.UnobservedError"/>（SafeFireAndForget 81 处调用点的单一咽喉，NonFatal）/
/// 手工 Record（Startup/Shutdown 路径的 catch 块）/ 非干净关机墓碑（Fatal，唯一能兜住原生 SIGABRT 的路径）。</para>
/// <para>去重与限流：同会话内按异常 hash（类型 + 栈前 3 帧 + 来源）合并为一行 upsert 计数；
/// 单 hash 上限 50 次、全会话上限 1000 次记录（防异常风暴把 crash.db 刷爆）。</para>
/// </summary>
public static class CrashRecorder
{
    private const int MaxPerHash = 50;
    private const int MaxPerSession = 1000;
    private const int MaxMessageLength = 2048;
    private const int MaxStackLength = 8000;

    private static readonly ConcurrentDictionary<string, int> HashCounts = new();

    private static CrashReportStore? _store;
    private static string _appName = "unknown";
    private static string _appVersion = string.Empty;
    private static string _os = string.Empty;
    private static Guid _sessionId;
    private static int _initialized;
    private static int _cleanMarked;
    private static int _totalRecords;

    /// <summary>
    /// 安装全局异常钩子 + 墓碑扫描 + 写本会话 marker。幂等（Interlocked）。两租户 Program.cs 的 Main 首行调用，
    /// 传 appName（"stockapp"/"qifu"）与租户数据目录（crash.db 落在那里）。
    /// </summary>
    public static void Initialize(string appName, string tenantDir)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        try
        {
            _appName = appName;
            _sessionId = Guid.NewGuid();
            _appVersion = ResolveAppVersion();
            _os = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
            _store = CrashReportStore.ForFile(Path.Combine(tenantDir, "crash.db"));

            // 墓碑扫描：上次会话没走 MarkCleanShutdown（clean=0）→ 合成 Fatal UncleanShutdown 记录。
            // 原生崩溃（SIGABRT 等）托管 handler 抓不到，这是唯一兜底（桌宠退出 saga 的教训）。
            var unclean = _store.FindUnprocessedUncleanSessions();
            if (unclean.Count > 0)
            {
                foreach (var s in unclean)
                {
                    _store.UpsertReport(new CrashReportRow
                    {
                        Id = Guid.NewGuid(),
                        SessionId = s.Id,
                        ExceptionHash = $"unclean:{s.Id}",
                        Source = CrashSource.UncleanShutdown,
                        Severity = CrashSeverity.Fatal,
                        ExceptionType = "UncleanShutdown",
                        Message = $"进程未走干净退出路径（app={s.App}，会话开始于 {s.StartedAt:yyyy-MM-dd HH:mm:ss}）——可能被强杀或原生崩溃",
                        Stack = string.Empty,
                        Count = 1,
                        App = s.App,
                        AppVersion = string.Empty,
                        Os = string.Empty,
                        OccurredAt = DateTimeOffset.UtcNow,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                }
                _store.MarkSessionsProcessed(unclean.Select(u => u.Id));
            }

            _store.InsertSessionMarker(_sessionId, appName);

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Record(CrashSource.UnhandledException, CrashSeverity.Fatal, e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Record(CrashSource.UnobservedTask, CrashSeverity.NonFatal, e.Exception);
                e.SetObserved();
            };
            // Mvvm 层的单一咽喉（SafeFireAndForget catch）→ 这里桥进遥测；Mvvm 保持零 recorder 依赖。
            // 全限定：System.Threading.Tasks.TaskExtensions（implicit using）与本类名撞名。
            Acme.Desktop.Mvvm.TaskExtensions.UnobservedError += ex =>
                Record(CrashSource.SafeFireAndForget, CrashSeverity.NonFatal, ex);
        }
        catch
        {
            /* 遥测初始化失败绝不阻断启动——_initialized 已置位（不重试），后续 Record 全部 no-op */
        }
    }

    /// <summary>
    /// 记录一条崩溃/错误（同步写本地 crash.db）。任意线程可调；recorder 未初始化时 no-op。
    /// </summary>
    public static void Record(string source, CrashSeverity severity, Exception? exception, string? message = null)
    {
        if (Volatile.Read(ref _initialized) == 0 || _store is null) return;
        try
        {
            var exceptionType = exception?.GetType().FullName;
            if (string.IsNullOrEmpty(exceptionType)) exceptionType = "Manual";
            var msg = message ?? exception?.Message ?? string.Empty;
            var stack = exception?.ToString() ?? string.Empty;
            var hash = HashOf(source, exceptionType, stack, msg);

            // 会话内限流：单 hash 50 次 / 全会话 1000 条（超限静默丢弃——行已在库里的计数保持）
            var count = HashCounts.AddOrUpdate(hash, 1, (_, v) => v + 1);
            if (count > MaxPerHash) return;
            if (Interlocked.Increment(ref _totalRecords) > MaxPerSession) return;

            _store.UpsertReport(new CrashReportRow
            {
                Id = Guid.NewGuid(),
                SessionId = _sessionId,
                ExceptionHash = hash,
                Source = source,
                Severity = severity,
                ExceptionType = exceptionType,
                Message = Truncate(msg, MaxMessageLength),
                Stack = Truncate(stack, MaxStackLength),
                Count = 1,
                App = _appName,
                AppVersion = _appVersion,
                Os = _os,
                DeviceName = null, // uploader 侧 best-effort 回填（device.db）
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        catch
        {
            /* recorder 永不外抛 */
        }
    }

    /// <summary>
    /// 标记本会话干净退出（写墓碑 clean=1，下次启动不再合成 UncleanShutdown）。
    /// 幂等 once-flag；调用点 = WorkbenchWindow.PrepareForShutdown / ExitApplication（最小完备集，
    /// 覆盖 dock-Quit / Cmd+Q / 桌宠菜单退出全部路径）。
    /// </summary>
    public static void MarkCleanShutdown()
    {
        if (Interlocked.Exchange(ref _cleanMarked, 1) != 0) return;
        try { _store?.MarkSessionClean(_sessionId); }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 去重键：FNV-1a 64（来源 + 异常类型 + 栈前 3 帧；无栈帧时退化用 message）。
    /// 只取浅层 3 帧——同一处异常稳定合并；深层帧受运行路径影响大，会把同源异常打散。
    /// </summary>
    internal static string HashOf(string source, string exceptionType, string stack, string message)
    {
        var frameText = new StringBuilder();
        var taken = 0;
        foreach (var line in stack.Split('\n'))
        {
            if (!line.Contains(" at ")) continue;
            frameText.Append(line.Trim()).Append('|');
            if (++taken >= 3) break;
        }
        if (taken == 0) frameText.Append(message); // 无堆栈（手工/墓碑记录）——退化用 message 参与 hash
        var text = source + "|" + exceptionType + "|" + frameText;

        unchecked
        {
            ulong h = 14695981039346656037;
            foreach (var ch in text)
            {
                h ^= ch;
                h *= 1099511628211;
            }
            return h.ToString("x16");
        }
    }

    private static string ResolveAppVersion()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            return entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? entry?.GetName().Version?.ToString()
                   ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];
}
