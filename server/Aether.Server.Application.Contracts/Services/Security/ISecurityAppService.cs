using Aether.Server.Application.Contracts.DTOs.Security;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Security;

public interface ISecurityAppService : ICrudAppService<
    SecurityDto,
    int,
    PagedAndSortedResultRequestDto,
    SecurityCreateDto,
    SecurityUpdateDto>
{}
