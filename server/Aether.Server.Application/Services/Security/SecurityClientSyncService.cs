using System;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Security;
using Aether.Server.Application.Contracts.Services.Security;
using Aether.Server.Domain.Services.Securities;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Services.Security;

[AllowAnonymous]
public class SecurityClientSyncService : ApplicationService, ISecurityClientSyncService
{
    private readonly ISecurityManager _securityManager;

    public SecurityClientSyncService(ISecurityManager securityManager)
    {
        _securityManager = securityManager;
    }

    public async Task<SecuritySyncResultDto> GetUpdatesAsync(DateTime? after = null)
    {
        var isFullSync = !after.HasValue || after.Value == DateTime.MinValue;
        var securities = isFullSync
            ? await _securityManager.GetAllAsync()
            : await _securityManager.GetUpdatedAfterAsync(after.Value);

        return new SecuritySyncResultDto
        {
            Securities = securities.Select(s => new SecuritySyncItemDto
            {
                Code = s.Code,
                Name = s.Name,
                Type = s.Type,
                Exchange = s.Exchange,
                BaseCode = s.BaseCode,
                UpdatedAt = s.UpdatedAt
            }).ToList(),
            ServerTime = DateTime.UtcNow,
            IsFullSync = isFullSync
        };
    }
}
