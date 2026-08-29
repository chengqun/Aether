using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.McphubToken;
using Aether.Server.Application.Contracts.Services.McphubToken;
using Aether.Server.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.McphubToken;

/// <summary>
/// mcphub 网关 JWT 管理：桌面端浏览器 OAuth 登录后上传，手机端拉取使用。
/// </summary>
[Authorize]
public class McphubTokenAppService : ApplicationService, IMcphubTokenAppService
{
    private readonly IRepository<AppMcphubToken, Guid> _repository;

    public McphubTokenAppService(IRepository<AppMcphubToken, Guid> repository)
    {
        _repository = repository;
    }

    /// <inheritdoc />
    [RequiresFeature("Yushu.SqlQuery")]
    public async Task SaveMyTokenAsync(SaveMcphubTokenDto input)
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (existing != null)
        {
            existing.Jwt = input.Jwt;
            existing.Username = input.Username;
            existing.CapturedAt = now;
            await _repository.UpdateAsync(existing);
        }
        else
        {
            var token = new AppMcphubToken(
                GuidGenerator.Create(),
                userId,
                input.Jwt,
                input.Username,
                now);
            await _repository.InsertAsync(token);
        }
    }

    /// <inheritdoc />
    [RequiresFeature("Yushu.DataQuery")]
    public async Task<McphubTokenDto> GetMyTokenAsync()
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        if (existing == null)
        {
            return new McphubTokenDto { HasToken = false };
        }

        return new McphubTokenDto
        {
            Jwt = existing.Jwt,
            Username = existing.Username,
            CapturedAt = existing.CapturedAt,
            HasToken = true,
        };
    }
}
