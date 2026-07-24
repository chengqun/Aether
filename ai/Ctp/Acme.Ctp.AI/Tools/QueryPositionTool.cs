using System.Collections.Generic;
using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.AI.Tools;

public class QueryPositionTool
{
    private readonly CtpSessionService _session;

    public QueryPositionTool(CtpSessionService session)
    {
        _session = session;
    }

    public async Task<IReadOnlyList<global::Ctp.Net.Core.Trader.Position>> QueryPositionsAsync(string? instrumentId = null)
    {
        return await _session.QueryInvestorPositionAsync(_session.BrokerId ?? "", _session.UserId ?? "", instrumentId);
    }
}
