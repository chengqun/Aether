using System;
using System.Collections.Generic;

namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// 崩溃上报客户端 DTO（**客户端手写——客户端永不引用 server Contracts**，house style 同
/// UpdateService/MomentService）。字段与服务端 <c>RecordCrashInputDto</c> 对齐（PascalCase JSON，
/// 服务端 JsonOptions PropertyNameCaseInsensitive 可绑定）。
/// </summary>
public sealed class RecordCrashesRequest
{
    public List<RecordCrashItemDto> Crashes { get; set; } = new();
}

public sealed class RecordCrashItemDto
{
    public string App { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    /// <summary>CrashSeverity 枚举值（0=Fatal / 1=NonFatal）。</summary>
    public int Severity { get; set; }

    public string ExceptionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Stack { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public DateTime OccurredAt { get; set; }
    public int Count { get; set; }
    public string? DeviceName { get; set; }
}

/// <summary>服务端回执（Accepted = 接受条数；非 2xx 时 PostAsync 返 null——静默保队列重试）。</summary>
public sealed class RecordCrashesAckDto
{
    public int Accepted { get; set; }
}
