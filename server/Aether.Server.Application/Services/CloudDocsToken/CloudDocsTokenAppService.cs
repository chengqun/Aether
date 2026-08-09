using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.CloudDocsToken;
using Aether.Server.Application.Contracts.Services.CloudDocsToken;
using Aether.Server.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp.Users;

namespace Aether.Server.Application.Services.CloudDocsToken;

/// <summary>
/// 360Teams 云文档 sid 管理：桌面端登录后上传，手机端拉取使用。
/// </summary>
[Authorize]
public class CloudDocsTokenAppService : ApplicationService, ICloudDocsTokenAppService
{
    private readonly IRepository<AppCloudDocsToken, Guid> _repository;

    public CloudDocsTokenAppService(IRepository<AppCloudDocsToken, Guid> repository)
    {
        _repository = repository;
    }

    /// <inheritdoc />
    [RequiresFeature("CloudDocs.Upload")]
    public async Task SaveMyTokenAsync(SaveCloudDocsTokenDto input)
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        if (existing != null)
        {
            existing.Sid = input.Sid;
            existing.Env = input.Env;
            existing.CapturedAt = input.CapturedAt;
            await _repository.UpdateAsync(existing);
        }
        else
        {
            var token = new AppCloudDocsToken(
                GuidGenerator.Create(),
                userId,
                input.Sid,
                input.Env,
                input.CapturedAt);
            await _repository.InsertAsync(token);
        }
    }

    /// <inheritdoc />
    [RequiresFeature("CloudDocs.Query")]
    public async Task<CloudDocsTokenDto> GetMyTokenAsync()
    {
        var userId = CurrentUser.GetId();

        var queryable = await _repository.GetQueryableAsync();
        var existing = await AsyncExecuter.FirstOrDefaultAsync(
            queryable.Where(t => t.UserId == userId));

        if (existing == null)
        {
            return new CloudDocsTokenDto { HasToken = false };
        }

        return new CloudDocsTokenDto
        {
            Sid = existing.Sid,
            Env = existing.Env,
            CapturedAt = existing.CapturedAt,
            HasToken = true,
        };
    }
}
