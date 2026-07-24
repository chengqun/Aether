using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;
using Ctp.Net.Core.MarketData;

namespace Acme.Ctp.AI.Tools;

public class QueryMarketDataTool
{
    private readonly CtpSessionService _session;

    public QueryMarketDataTool(CtpSessionService session)
    {
        _session = session;
    }

    public Task<TickData?> QueryLatestTickAsync(string instrumentId)
    {
        return Task.FromResult(_session.GetLatestTick(instrumentId));
    }

    public Task<IReadOnlyList<TickData>> QueryAllLatestTicksAsync()
    {
        var ticks = _session.GetAllLatestTicks().Values.ToList();
        return Task.FromResult<IReadOnlyList<TickData>>(ticks);
    }
}
