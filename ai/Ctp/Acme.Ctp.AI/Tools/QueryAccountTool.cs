using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryAccountTool
{
    private readonly ICtpTradingService _service;

    public QueryAccountTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<CtpAccount> QueryAccountAsync()
    {
        return await _service.QueryAccountAsync();
    }
}