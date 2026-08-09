using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.VersionConfig;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.VersionConfig;

public interface IVersionConfigAppService : ICrudAppService<
    VersionConfigDto,
    Guid,
    PagedAndSortedResultRequestDto,
    VersionConfigCreateDto,
    VersionConfigUpdateDto>
{
    Task<CheckUpdateResultDto> CheckUpdateAsync(CheckUpdateInputDto input);
    Task PublishAsync(VersionConfigPublishDto input);
}