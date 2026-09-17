using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Crash;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Crash;

public interface ICrashAppService : IApplicationService
{
    /// <summary>
    /// 客户端批量上报崩溃/错误记录（bearer 认证；UserId 取当前登录用户）
    /// </summary>
    Task<RecordCrashesResultDto> RecordCrashesAsync(RecordCrashesInputDto input);

    /// <summary>
    /// 崩溃汇总（按异常类型 + 来源分组，需 Crash.View 权限）
    /// </summary>
    Task<List<CrashSummaryDto>> GetCrashSummaryAsync(string? app = null, string? source = null, int? severity = null, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    /// 崩溃明细（需 Crash.View 权限）
    /// </summary>
    Task<List<CrashDetailDto>> GetCrashDetailsAsync(string? app = null, string? source = null, int? severity = null, DateTime? startTime = null, DateTime? endTime = null);
}
