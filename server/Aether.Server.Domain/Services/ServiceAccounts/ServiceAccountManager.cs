using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Aether.Server.Domain.Services.ServiceAccounts;

/// <summary>
/// 服务号管理器接口
/// </summary>
public interface IServiceAccountManager
{
    Task<AppServiceAccount> CreateAsync(string name, Guid ownerId, string? avatarUrl = null, string? description = null, string category = "资讯");
    Task<AppServiceAccount> UpdateAsync(Guid id, string name, string? avatarUrl, string? description, string category, bool isVerified, string? welcomeMessage);
    Task DeleteAsync(Guid id);
    Task<AppServiceAccount> GetAsync(Guid id);
    Task<List<AppServiceAccount>> GetAllAsync();
    Task<List<AppServiceAccount>> SearchAsync(string keyword);
    Task<List<AppServiceAccount>> GetByCategoryAsync(string category);
}

/// <summary>
/// 服务号管理器
/// </summary>
public class ServiceAccountManager : DomainService, IServiceAccountManager
{
    private readonly IRepository<AppServiceAccount, Guid> _repository;

    public ServiceAccountManager(IRepository<AppServiceAccount, Guid> repository)
    {
        _repository = repository;
    }

    public async Task<AppServiceAccount> CreateAsync(string name, Guid ownerId, string? avatarUrl = null, string? description = null, string category = "资讯")
    {
        var id = GuidGenerator.Create();
        var serviceAccount = new AppServiceAccount(id, name, ownerId, category)
        {
            AvatarUrl = avatarUrl,
            Description = description
        };
        return await _repository.InsertAsync(serviceAccount);
    }

    public async Task<AppServiceAccount> UpdateAsync(Guid id, string name, string? avatarUrl, string? description, string category, bool isVerified, string? welcomeMessage)
    {
        var serviceAccount = await _repository.GetAsync(id);
        serviceAccount.Update(name, avatarUrl, description, category, isVerified, welcomeMessage);
        return await _repository.UpdateAsync(serviceAccount);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _repository.DeleteAsync(id);
    }

    public async Task<AppServiceAccount> GetAsync(Guid id)
    {
        return await _repository.GetAsync(id);
    }

    public async Task<List<AppServiceAccount>> GetAllAsync()
    {
        return await _repository.GetListAsync();
    }

    public async Task<List<AppServiceAccount>> SearchAsync(string keyword)
    {
        return await _repository.GetListAsync(
            sa => sa.Name.Contains(keyword) || (sa.Description != null && sa.Description.Contains(keyword))
        );
    }

    public async Task<List<AppServiceAccount>> GetByCategoryAsync(string category)
    {
        return await _repository.GetListAsync(sa => sa.Category == category);
    }
}
