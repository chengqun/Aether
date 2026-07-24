using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryPositionTool
{
    private readonly ICtpTradingService _service;

    public QueryPositionTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<IReadOnlyList<CtpPosition>> QueryPositionsAsync(string? instrumentId = null)
    {
        return await _service.QueryPositionAsync(instrumentId);
    }
}