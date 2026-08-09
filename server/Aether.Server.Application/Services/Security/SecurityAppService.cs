using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Security;
using Aether.Server.Application.Contracts.Services.Security;
using Aether.Server.Domain.Entities;
using Aether.Server.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Application.Dtos;

namespace Aether.Server.Application.Services.Security
{
    [Authorize]
    public class SecurityAppService : CrudAppService<AppSecurity, SecurityDto, int, PagedAndSortedResultRequestDto, SecurityCreateDto, SecurityUpdateDto>, ISecurityAppService
    {
        public SecurityAppService(IRepository<AppSecurity, int> repository)
            : base(repository)
        {
            GetPolicyName = AbpPermissions.Securities.Default;
            GetListPolicyName = AbpPermissions.Securities.Default;
            CreatePolicyName = AbpPermissions.Securities.Create;
            UpdatePolicyName = AbpPermissions.Securities.Update;
            DeletePolicyName = AbpPermissions.Securities.Delete;
        }

        [Authorize(AbpPermissions.Securities.Create)]
        public override Task<SecurityDto> CreateAsync(SecurityCreateDto input)
        {
            return base.CreateAsync(input);
        }

        [Authorize(AbpPermissions.Securities.Update)]
        public override Task<SecurityDto> UpdateAsync(int id, SecurityUpdateDto input)
        {
            return base.UpdateAsync(id, input);
        }

        [Authorize(AbpPermissions.Securities.Delete)]
        public override Task DeleteAsync(int id)
        {
            return base.DeleteAsync(id);
        }

        [Authorize(AbpPermissions.Securities.Default)]
        public override Task<SecurityDto> GetAsync(int id)
        {
            return base.GetAsync(id);
        }

        [Authorize(AbpPermissions.Securities.Default)]
        public override Task<PagedResultDto<SecurityDto>> GetListAsync(PagedAndSortedResultRequestDto input)
        {
            return base.GetListAsync(input);
        }
    }
}