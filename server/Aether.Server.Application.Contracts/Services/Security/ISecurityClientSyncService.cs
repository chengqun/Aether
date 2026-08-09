using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Security;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Security;

public interface ISecurityClientSyncService : IApplicationService
{
    Task<SecuritySyncResultDto> GetUpdatesAsync(DateTime? after = null);
}