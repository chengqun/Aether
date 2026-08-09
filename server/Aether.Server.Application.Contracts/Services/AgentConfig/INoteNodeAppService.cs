using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.AgentConfig;

/// <summary>
/// NoteNode 管理服务（Blazor admin CRUD）。替代旧 IAgentConfigAppService。
/// host admin 跨租户可见；租户 admin 仅本租户。
/// </summary>
public interface INoteNodeAppService : ICrudAppService<
    NoteNodeDto,
    Guid,
    PagedAndSortedResultRequestDto,
    NoteNodeCreateDto,
    NoteNodeUpdateDto>
{
}
