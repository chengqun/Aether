using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;

namespace Acme.Ctp.AI.Tools;

public class OrderActionTool
{
    private readonly CtpSessionService _session;

    public OrderActionTool(CtpSessionService session)
    {
        _session = session;
    }

    public async Task<string> OrderActionAsync(
        int frontId, int sessionId, string orderRef, string exchangeId,
        bool requireConfirmation = true)
    {
        if (requireConfirmation)
        {
            return $"待确认撤单: frontId={frontId} sessionId={sessionId} orderRef={orderRef} exchangeId={exchangeId}";
        }

        await _session.OrderActionAsync(_session.BrokerId ?? "", _session.UserId ?? "", frontId, sessionId, orderRef, exchangeId);

        return $"已撤单: orderRef={orderRef}";
    }
}
