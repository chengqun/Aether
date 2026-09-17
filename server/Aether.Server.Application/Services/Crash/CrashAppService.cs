using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Crash;
using Aether.Server.Application.Contracts.Services.Crash;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.Crash;

/// <summary>
/// 崩溃遥测（镜像 AgentUsageAppService 模式）。上报端点刻意走类级 [Authorize] bearer
/// 而非 AgentUsage 的 AllowAnonymous+X-Publish-Token：那个 token 从未配进任何 appsettings
/// （端点实际不可用），且客户端 IAppServerHttpClient 不支持自定义 header——桌面经 bearer
/// 上报，未登录时本地排队等首次登录。
/// </summary>
[Authorize]
public class CrashAppService : ApplicationService, ICrashAppService
{
    private readonly IRepository<AppCrashRecord, Guid> _repository;
    private readonly IdentityUserManager _identityUserManager;

    public CrashAppService(
        IRepository<AppCrashRecord, Guid> repository,
        IdentityUserManager identityUserManager)
    {
        _repository = repository;
        _identityUserManager = identityUserManager;
    }

    public async Task<RecordCrashesResultDto> RecordCrashesAsync(RecordCrashesInputDto input)
    {
        if (input.Crashes is null || input.Crashes.Count == 0)
        {
            return new RecordCrashesResultDto { Accepted = 0 };
        }

        // 只取类级 [Authorize]（桌面用户非 admin，不加权限 attribute——同 AgentUsage 上报端点的可达性设计）
        var userId = CurrentUser.GetId();
        var records = input.Crashes
            .Where(c => !string.IsNullOrWhiteSpace(c.ExceptionType))
            .Select(c => new AppCrashRecord(
                GuidGenerator.Create(),
                userId,
                Truncate(c.App, 32),
                Truncate(c.AppVersion, 64),
                Truncate(c.Os, 256),
                Truncate(c.Source, 32),
                (CrashSeverity)(c.Severity == 1 ? 1 : 0),
                Truncate(c.ExceptionType, 256),
                Truncate(c.Message, 2048),
                Truncate(c.Stack, 8000),
                c.SessionId,
                c.OccurredAt == default ? Clock.Now : c.OccurredAt,
                c.Count < 1 ? 1 : c.Count,
                string.IsNullOrWhiteSpace(c.DeviceName) ? null : Truncate(c.DeviceName!, 128)))
            .ToList();

        if (records.Count > 0)
        {
            await _repository.InsertManyAsync(records, autoSave: true);
        }

        return new RecordCrashesResultDto { Accepted = records.Count };
    }

    [Authorize(AbpPermissions.Crash.View)]
    public async Task<List<CrashSummaryDto>> GetCrashSummaryAsync(
        string? app = null,
        string? source = null,
        int? severity = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var records = await GetFilteredAsync(app, source, severity, startTime, endTime);

        return records
            .GroupBy(r => new { r.ExceptionType, r.Source })
            .Select(g => new CrashSummaryDto
            {
                ExceptionType = g.Key.ExceptionType,
                Source = g.Key.Source,
                TotalCount = g.Sum(r => (long)r.Count),
                RecordCount = g.Count(),
                SessionCount = g.Select(r => r.SessionId).Distinct().Count(),
                Apps = string.Join(", ", g.Select(r => r.App).Distinct()),
                LastOccurredAt = g.Max(r => r.OccurredAt)
            })
            .OrderByDescending(s => s.LastOccurredAt)
            .ToList();
    }

    [Authorize(AbpPermissions.Crash.View)]
    public async Task<List<CrashDetailDto>> GetCrashDetailsAsync(
        string? app = null,
        string? source = null,
        int? severity = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var records = await GetFilteredAsync(app, source, severity, startTime, endTime);

        // 批量查询用户名称（同 AgentUsage 的解析形状）
        var userIds = records.Select(r => r.UserId).Distinct().ToList();
        var userNames = new Dictionary<Guid, string>();
        foreach (var uid in userIds)
        {
            var user = await _identityUserManager.FindByIdAsync(uid.ToString());
            userNames[uid] = user != null
                ? user.UserName ?? user.Email ?? uid.ToString()
                : uid.ToString();
        }

        return records.Select(r => new CrashDetailDto
        {
            Id = r.Id,
            UserId = r.UserId,
            UserName = userNames.GetValueOrDefault(r.UserId, r.UserId.ToString()),
            App = r.App,
            AppVersion = r.AppVersion,
            Os = r.Os,
            Source = r.Source,
            Severity = (int)r.Severity,
            ExceptionType = r.ExceptionType,
            Message = r.Message,
            Stack = r.Stack,
            SessionId = r.SessionId,
            OccurredAt = r.OccurredAt,
            Count = r.Count,
            DeviceName = r.DeviceName,
            CreationTime = r.CreationTime
        }).ToList();
    }

    private async Task<List<AppCrashRecord>> GetFilteredAsync(
        string? app, string? source, int? severity, DateTime? startTime, DateTime? endTime)
    {
        var queryable = await _repository.GetQueryableAsync();

        if (!string.IsNullOrWhiteSpace(app))
        {
            queryable = queryable.Where(r => r.App == app);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            queryable = queryable.Where(r => r.Source == source);
        }

        if (severity.HasValue)
        {
            queryable = queryable.Where(r => (int)r.Severity == severity.Value);
        }

        if (startTime.HasValue)
        {
            queryable = queryable.Where(r => r.OccurredAt >= startTime.Value);
        }

        if (endTime.HasValue)
        {
            queryable = queryable.Where(r => r.OccurredAt <= endTime.Value);
        }

        queryable = queryable.OrderByDescending(r => r.OccurredAt);

        return await AsyncExecuter.ToListAsync(queryable);
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= maxLength ? value : value[..maxLength];
}
