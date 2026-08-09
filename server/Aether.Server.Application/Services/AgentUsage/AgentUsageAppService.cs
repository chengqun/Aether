using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentUsage;
using Aether.Server.Application.Contracts.Services.AgentUsage;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Identity;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.AgentUsage;

[Authorize]
public class AgentUsageAppService : ApplicationService, IAgentUsageAppService
{
    private readonly IRepository<AppAgentUsageRecord, Guid> _repository;
    private readonly IConfiguration _configuration;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IdentityUserManager _identityUserManager;

    public AgentUsageAppService(
        IRepository<AppAgentUsageRecord, Guid> repository,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        IdentityUserManager identityUserManager)
    {
        _repository = repository;
        _configuration = configuration;
        _httpContextAccessor = httpContextAccessor;
        _identityUserManager = identityUserManager;
    }

    [AllowAnonymous]
    public async Task RecordUsageAsync(RecordAgentUsageDto input)
    {
        // 验证 X-Publish-Token（与 VersionConfigAppService.PublishAsync 相同模式）
        var token = _configuration["AgentUsage:RecordToken"];
        if (string.IsNullOrWhiteSpace(token)
            || !_httpContextAccessor.HttpContext!.Request.Headers.TryGetValue("X-Publish-Token", out var requestToken)
            || requestToken != token)
        {
            throw new UnauthorizedAccessException("Invalid publish token");
        }

        var userId = CurrentUser.GetId();
        var record = new AppAgentUsageRecord(
            GuidGenerator.Create(),
            userId,
            input.AgentName,
            (AgentKind)input.AgentKind,
            input.SessionId,
            input.InputTokens,
            input.OutputTokens,
            input.TotalTokens
        );

        await _repository.InsertAsync(record);
    }

    public async Task<List<AgentUsageSummaryDto>> GetMyUsageSummaryAsync()
    {
        var userId = CurrentUser.GetId();
        var queryable = await _repository.GetQueryableAsync();
        var records = await AsyncExecuter.ToListAsync(
            queryable.Where(r => r.UserId == userId)
        );

        return records
            .GroupBy(r => new { r.AgentName, r.AgentKind })
            .Select(g => new AgentUsageSummaryDto
            {
                AgentName = g.Key.AgentName,
                AgentKind = (int)g.Key.AgentKind,
                TotalInputTokens = g.Sum(r => r.InputTokens),
                TotalOutputTokens = g.Sum(r => r.OutputTokens),
                TotalTokens = g.Sum(r => r.TotalTokens),
                CallCount = g.Count()
            })
            .OrderByDescending(s => s.TotalTokens)
            .ToList();
    }

    [Authorize(AbpPermissions.AgentUsage.View)]
    public async Task<List<AgentUsageDetailDto>> GetUsageDetailsAsync(
        string? agentName = null,
        DateTime? startTime = null,
        DateTime? endTime = null)
    {
        var queryable = await _repository.GetQueryableAsync();

        if (!string.IsNullOrWhiteSpace(agentName))
        {
            queryable = queryable.Where(r => r.AgentName == agentName);
        }

        if (startTime.HasValue)
        {
            queryable = queryable.Where(r => r.CreationTime >= startTime.Value);
        }

        if (endTime.HasValue)
        {
            queryable = queryable.Where(r => r.CreationTime <= endTime.Value);
        }

        queryable = queryable.OrderByDescending(r => r.CreationTime);

        var records = await AsyncExecuter.ToListAsync(queryable);

        // 批量查询用户名称
        var userIds = records.Select(r => r.UserId).Distinct().ToList();
        var userNames = new Dictionary<Guid, string>();
        foreach (var uid in userIds)
        {
            var user = await _identityUserManager.FindByIdAsync(uid.ToString());
            if (user != null)
            {
                userNames[uid] = user.UserName ?? user.Email ?? uid.ToString();
            }
            else
            {
                userNames[uid] = uid.ToString();
            }
        }

        return records.Select(r => new AgentUsageDetailDto
        {
            Id = r.Id,
            UserId = r.UserId,
            UserName = userNames.GetValueOrDefault(r.UserId, r.UserId.ToString()),
            AgentName = r.AgentName,
            AgentKind = (int)r.AgentKind,
            SessionId = r.SessionId,
            InputTokens = r.InputTokens,
            OutputTokens = r.OutputTokens,
            TotalTokens = r.TotalTokens,
            CreationTime = r.CreationTime
        }).ToList();
    }
}