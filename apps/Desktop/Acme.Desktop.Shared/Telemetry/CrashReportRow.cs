namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// crash_report 表的一行（客户端本地崩溃队列条目）。Guid/时间以字符串落库（同 device.db 惯例），
/// created_at/occurred_at 存 UTC ISO "O" 串（同 offset 串的字符串比较 = 时刻比较，供排序/清理用）。
/// </summary>
public sealed class CrashReportRow
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }

    /// <summary>去重键：异常类型 + 栈前 3 帧（+ 来源）的 FNV-1a hash。UNIQUE(session_id, exception_hash) 约束用它。</summary>
    public string ExceptionHash { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;
    public CrashSeverity Severity { get; init; }
    public string ExceptionType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Stack { get; init; } = string.Empty;

    /// <summary>会话内同 hash 累计次数（upsert 每次冲突 +1，recorder 侧限流后单行计数）。</summary>
    public int Count { get; init; }

    public string App { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;

    /// <summary>设备名：记录时留空，uploader 侧 best-effort 回填（device.db）。</summary>
    public string? DeviceName { get; set; }

    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Attempts { get; init; }
}

/// <summary>session_marker 表中未走干净退出且未处理的旧会话（墓碑扫描结果）。</summary>
public sealed record UncleanSession(Guid Id, string App, DateTimeOffset StartedAt);
