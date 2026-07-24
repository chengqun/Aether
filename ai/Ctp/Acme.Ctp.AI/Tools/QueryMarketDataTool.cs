using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryMarketDataTool
{
    private readonly ICtpTradingService _service;

    public QueryMarketDataTool(ICtpTradingService service)
    {
        _service = service;
    }

    public Task<CtpTick?> QueryLatestTickAsync(string instrumentId)
    {
        return Task.FromResult(_service.GetLatestTick(instrumentId));
    }

    public Task<IReadOnlyList<CtpTick>> QueryAllLatestTicksAsync()
    {
        var ticks = _service.GetAllLatestTicks().Values.ToList();
        return Task.FromResult<IReadOnlyList<CtpTick>>(ticks);
    }
}