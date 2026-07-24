using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SecurityEntity = Acme.Securities.Domain.Security;
using Acme.Securities.Domain;

namespace Acme.Securities.Application;

public class SecurityService : ISecurityService
{
    private readonly ISecurityManager _securityManager;

    public SecurityService(ISecurityManager securityManager)
    {
        _securityManager = securityManager;
    }

    public async Task<SecurityDto> AddSecurityAsync(SecurityDto dto)
    {
        var security = ToEntity(dto);
        var result = await _securityManager.AddSecurityAsync(security);
        return ToDto(result);
    }

    public async Task<SecurityDto> UpdateOrAddSecurityAsync(SecurityDto dto)
    {
        var security = ToEntity(dto);
        var result = await _securityManager.UpdateOrAddSecurityAsync(security);
        return ToDto(result);
    }

    public async Task AddSecuritiesAsync(IEnumerable<SecurityDto> dtos)
    {
        var securities = dtos.Select(ToEntity);
        await _securityManager.AddSecuritiesAsync(securities);
    }

    public async Task AddSecuritiesAsync(IEnumerable<SecurityDto> dtos, int batchSize)
    {
        var securities = dtos.Select(ToEntity);
        await _securityManager.AddSecuritiesAsync(securities, batchSize);
    }

    public async Task<List<SecurityDto>> SearchSecuritiesAsync(string keyword)
    {
        var securities = await _securityManager.SearchSecuritiesAsync(keyword);
        return securities.Select(ToDto).ToList();
    }

    private static SecurityDto ToDto(SecurityEntity security) => new()
    {
        Id = security.Id,
        Code = security.Code,
        Name = security.Name,
        Type = security.Type,
        Exchange = security.Exchange,
        BaseCode = security.BaseCode,
        UpdatedAt = security.UpdatedAt
    };

    private static SecurityEntity ToEntity(SecurityDto dto) => new(
        dto.Id, dto.Code, dto.Name, dto.Type, dto.Exchange, dto.BaseCode, dto.UpdatedAt);
}
