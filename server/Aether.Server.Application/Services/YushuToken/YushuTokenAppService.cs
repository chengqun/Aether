using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.YushuToken;
using Aether.Server.Application.Contracts.Services.YushuToken;
using Aether.Server.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.YushuToken;

/// <summary>
/// 毓数平台 Token 管理：桌面端认证后上传，手机端拉取使用。
/// </summary>
[Authorize]
public class YushuTokenAppService : ApplicationService, IYushuTokenAppService
{
    private readonly IRepository<AppYushuToken, Guid> _repository;

    public YushuTokenAppService(IRepository<AppYushuToken, Guid> repository)
    {
        _repository = repository;
    }

    /// <inheritdoc />
    [RequiresFeature("Yushu.SqlQuery")]
    public async Task SaveMyTokenAsync(SaveYushuTokenDto input)
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        if (existing != null)
        {
            // 更新已有记录
            existing.AccessToken = input.AccessToken;
            existing.RefreshToken = input.RefreshToken;
            existing.ExpiresAt = input.ExpiresAt;
            existing.TokenType = input.TokenType;
            existing.ClientConfigJson = input.ClientConfigJson;
            await _repository.UpdateAsync(existing);
        }
        else
        {
            // 创建新记录
            var token = new AppYushuToken(
                GuidGenerator.Create(),
                userId,
                input.AccessToken,
                input.RefreshToken,
                input.ExpiresAt,
                input.TokenType,
                input.ClientConfigJson);
            await _repository.InsertAsync(token);
        }
    }

    /// <inheritdoc />
    [RequiresFeature("Yushu.DataQuery")]
    public async Task<YushuTokenDto> GetMyTokenAsync()
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        if (existing == null)
        {
            return new YushuTokenDto { HasToken = false };
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new YushuTokenDto
        {
            AccessToken = existing.AccessToken,
            ExpiresAt = existing.ExpiresAt,
            TokenType = existing.TokenType,
            HasToken = true,
            IsExpired = existing.ExpiresAt <= now,
        };
    }
}
