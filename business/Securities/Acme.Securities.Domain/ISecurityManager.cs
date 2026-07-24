using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Securities.Domain;

public interface ISecurityManager
{
    Task<Security> AddSecurityAsync(Security security);
    Task<Security> UpdateOrAddSecurityAsync(Security security);
    Task AddSecuritiesAsync(IEnumerable<Security> securities);
    Task AddSecuritiesAsync(IEnumerable<Security> securities, int batchSize);
    Task<List<Security>> SearchSecuritiesAsync(string keyword);
    Task<Security?> GetSecurityByIdAsync(int id);
    Task<Security?> GetSecurityByCodeAsync(string code);
    Task<List<Security>> GetSecuritiesByExchangeAsync(string exchange);
    Task<List<Security>> GetSecuritiesByTypeAsync(string type);
    /// <summary>
    /// 取每个期货品种（BaseCode）月份最大的合约（主期货/主力合约）。
    /// 期货代码后缀为 YYMM（如 rb2610 = 2026年10月），按数值最大取最新月份。
    /// </summary>
    Task<List<Security>> GetMainFuturesAsync();
    Task<bool> ExistsAsync(string code);
    Task UpdateSecurityAsync(Security security);
    Task DeleteSecurityAsync(int id);
}
