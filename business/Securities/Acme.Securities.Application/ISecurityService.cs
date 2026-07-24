using System.Collections.Generic;
using System.Threading.Tasks;

namespace Acme.Securities.Application;

public interface ISecurityService
{
    Task<SecurityDto> AddSecurityAsync(SecurityDto dto);
    Task<SecurityDto> UpdateOrAddSecurityAsync(SecurityDto dto);
    Task AddSecuritiesAsync(IEnumerable<SecurityDto> dtos);
    Task AddSecuritiesAsync(IEnumerable<SecurityDto> dtos, int batchSize);
    Task<List<SecurityDto>> SearchSecuritiesAsync(string keyword);
}
