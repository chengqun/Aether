using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.AI.Tools;

public class QueryAccountTool
{
    private readonly CtpSessionService _session;

    public QueryAccountTool(CtpSessionService session)
    {
        _session = session;
    }

    public async Task<global::Ctp.Net.Core.Trader.Account> QueryAccountAsync()
    {
        return await _session.QueryTradingAccountAsync(_session.BrokerId ?? "", _session.UserId ?? "");
    }
}
