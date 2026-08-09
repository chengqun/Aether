using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;

namespace Aether.Server.Domain.Services.Securities
{

    public interface ISecurityManager
    {
        Task<AppSecurity?> FindAsync(string query);
        Task<AppSecurity?> GetByIdAsync(int id);
        Task<List<AppSecurity>> SearchAsync(string query);
        Task<List<AppSecurity>> GetAllAsync();
        Task<AppSecurity?> GetByCodeAndTypeAsync(string code, string type);
        Task<Dictionary<string, AppSecurity>> GetByCodeAndTypeBatchAsync(List<string> codes, string type);
        Task<(int added, int updated)> UpsertRangeAsync(List<AppSecurity> securities);
        Task<List<AppSecurity>> GetUpdatedAfterAsync(DateTime after);
    }
}