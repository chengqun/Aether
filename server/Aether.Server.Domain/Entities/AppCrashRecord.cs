using System;
using Aether.Server.Domain.Shared.Enums;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities;

/// <summary>
/// 桌面客户端崩溃/错误上报记录（崩溃遥测）。镜像 <see cref="AppAgentUsageRecord"/> 模式。
/// 客户端本地 crash.db 排队、登录后批量上报；上报端点走类级 [Authorize] bearer（UserId 取 CurrentUser）。
/// Count 为客户端会话内按异常 hash 去重后的累计次数（同一异常反复出现合并为一条，count 累加）。
/// </summary>
public class AppCrashRecord : CreationAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>上报来源应用（"stockapp" / "qifu"）。</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>客户端版本（入口程序集 AssemblyInformationalVersion）。</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>操作系统描述（RuntimeInformation.OSDescription + 架构）。</summary>
    public string Os { get; set; } = string.Empty;

    /// <summary>捕获来源：UnhandledException / UnobservedTask / SafeFireAndForget / Startup / Shutdown / UncleanShutdown。</summary>
    public string Source { get; set; } = string.Empty;

    public CrashSeverity Severity { get; set; }

    public string ExceptionType { get; set; } = string.Empty;

    /// <summary>异常消息（客户端截断至 2048）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>异常堆栈（客户端截断至 8000）。</summary>
    public string Stack { get; set; } = string.Empty;

    /// <summary>客户端进程会话 id（与本地 session_marker 对应）。</summary>
    public Guid SessionId { get; set; }

    /// <summary>客户端本地异常发生时间（客户端时钟）。</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>会话内同 hash 异常的累计次数（去重计数）。</summary>
    public int Count { get; set; }

    /// <summary>设备名（device.db 注册的桌面设备，best-effort 可空）。</summary>
    public string? DeviceName { get; set; }

    protected AppCrashRecord() { }

    public AppCrashRecord(
        Guid id,
        Guid userId,
        string app,
        string appVersion,
        string os,
        string source,
        CrashSeverity severity,
        string exceptionType,
        string message,
        string stack,
        Guid sessionId,
        DateTime occurredAt,
        int count,
        string? deviceName) : base(id)
    {
        UserId = userId;
        App = app;
        AppVersion = appVersion;
        Os = os;
        Source = source;
        Severity = severity;
        ExceptionType = exceptionType;
        Message = message;
        Stack = stack;
        SessionId = sessionId;
        OccurredAt = occurredAt;
        Count = count;
        DeviceName = deviceName;
    }
}
