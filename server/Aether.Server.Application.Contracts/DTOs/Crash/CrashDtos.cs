using System;
using System.Collections.Generic;

namespace Aether.Server.Application.Contracts.DTOs.Crash;

/// <summary>
/// 客户端上报单条崩溃/错误记录 DTO
/// </summary>
public class RecordCrashInputDto
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

    /// <summary>会话内同 hash 异常累计次数（客户端去重计数）。</summary>
    public int Count { get; set; }

    public string? DeviceName { get; set; }
}

/// <summary>
/// 客户端批量上报 DTO（本地队列一次 drain 一批）
/// </summary>
public class RecordCrashesInputDto
{
    public List<RecordCrashInputDto> Crashes { get; set; } = new();
}

/// <summary>
/// 批量上报回执
/// </summary>
public class RecordCrashesResultDto
{
    public int Accepted { get; set; }
}

/// <summary>
/// 崩溃汇总 DTO（按 ExceptionType + Source 分组）
/// </summary>
public class CrashSummaryDto
{
    public string ExceptionType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;

    /// <summary>出现次数（按 Count 累计的总出现次数）。</summary>
    public long TotalCount { get; set; }

    /// <summary>上报记录条数。</summary>
    public int RecordCount { get; set; }

    /// <summary>涉及会话数（去重 SessionId）。</summary>
    public int SessionCount { get; set; }

    /// <summary>涉及应用（逗号拼接去重）。</summary>
    public string Apps { get; set; } = string.Empty;

    /// <summary>最近一次发生时间（客户端 OccurredAt）。</summary>
    public DateTime LastOccurredAt { get; set; }
}

/// <summary>
/// 崩溃明细 DTO
/// </summary>
public class CrashDetailDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
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
    public DateTime CreationTime { get; set; }
}
