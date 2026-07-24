using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Securities.Domain;

public interface ISecurityRepository
{
    Task<List<Security>> GetAllAsync();
    Task<Security?> GetByIdAsync(int id);
    Task<Security?> GetByCodeAsync(string code);
    Task<List<Security>> GetByExchangeAsync(string exchange);
    Task<List<Security>> GetByTypeAsync(string type);
    Task<bool> ExistsAsync(string code);
    Task<Security> AddAsync(Security security);
    Task<Security> UpdateAsync(Security security);
    Task DeleteAsync(int id);
    Task<List<Security>> SearchAsync(string keyword);
    Task AddRangeAsync(IEnumerable<Security> securities);
    Task AddRangeAsync(IEnumerable<Security> securities, int batchSize);
}
