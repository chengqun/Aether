using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Securities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Aether.Server.Domain.Services.Securities
{

public class SecurityManager : DomainService, ISecurityManager
{
    private readonly IRepository<AppSecurity, int> _securityRepository;

    public SecurityManager(IRepository<AppSecurity, int> securityRepository)
    {
            _securityRepository = securityRepository;
    }

    public async Task<AppSecurity?> FindAsync(string query)
    {
        // 服务端过滤：将查询下推到 SQL WHERE 子句，避免全表加载
        return await _securityRepository.FirstOrDefaultAsync(s =>
            s.Code == query || s.Name.Contains(query));
    }

    public async Task<AppSecurity?> GetByIdAsync(int id)
    {
        return await _securityRepository.GetAsync(id, includeDetails: false);
    }

    public async Task<List<AppSecurity>> SearchAsync(string query)
    {
        // 服务端过滤 + 限制返回数量，避免全表加载
        var results = await _securityRepository.GetListAsync(s =>
            s.Code.Contains(query) || s.Name.Contains(query));
        return results.Take(20).ToList();
    }

    public async Task<List<AppSecurity>> GetAllAsync()
    {
        return await _securityRepository.GetListAsync();
    }

    public async Task<AppSecurity?> GetByCodeAndTypeAsync(string code, string type)
    {
        // 使用 FirstOrDefaultAsync 替代 GetListAsync + FirstOrDefault
        return await _securityRepository.FirstOrDefaultAsync(s => s.Code == code && s.Type == type);
    }

    public async Task<Dictionary<string, AppSecurity>> GetByCodeAndTypeBatchAsync(List<string> codes, string type)
    {
        // 批量查询：一次 SQL 获取所有匹配的证券，避免 N+1
        var list = await _securityRepository.GetListAsync(s => codes.Contains(s.Code) && s.Type == type);
        return list.ToDictionary(s => s.Code);
    }

    public async Task<(int added, int updated)> UpsertRangeAsync(List<AppSecurity> securities)
    {
        var codes = securities.Select(s => s.Code).Distinct().ToList();
        var types = securities.Select(s => s.Type).Distinct().ToList();

        var existing = await _securityRepository.GetListAsync(s => codes.Contains(s.Code) && types.Contains(s.Type));

        var existingDict = existing.ToDictionary(s => $"{s.Code}|{s.Type}");
        var toInsert = new List<AppSecurity>();
        var updated = 0;

        foreach (var security in securities)
        {
            var key = $"{security.Code}|{security.Type}";
            if (existingDict.TryGetValue(key, out var existingEntity))
            {
                existingEntity.UpdateDetails(security.Name, security.Exchange, security.BaseCode);
                updated++;
            }
            else
            {
                security.MarkUpdated();
                toInsert.Add(security);
            }
        }

        if (toInsert.Count > 0)
        {
            await _securityRepository.InsertManyAsync(toInsert, autoSave: true);
        }

        if (updated > 0)
        {
            await _securityRepository.UpdateManyAsync(existing, autoSave: true);
        }

        return (toInsert.Count, updated);
    }

    public async Task<List<AppSecurity>> GetUpdatedAfterAsync(DateTime after)
    {
        return await _securityRepository.GetListAsync(s => s.UpdatedAt > after);
    }
}
}
