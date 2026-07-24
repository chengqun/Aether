using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class OrderActionTool
{
    private readonly ICtpTradingService _service;

    public OrderActionTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<string> OrderActionAsync(
        int frontId, int sessionId, string orderRef, string exchangeId,
        bool requireConfirmation = true)
    {
        if (requireConfirmation)
        {
            return $"待确认撤单: frontId={frontId} sessionId={sessionId} orderRef={orderRef} exchangeId={exchangeId}";
        }

        await _service.CancelOrderAsync(new CtpOrderRef(
            _service.BrokerId ?? "", _service.UserId ?? "", frontId, sessionId, orderRef, exchangeId));

        return $"已撤单: orderRef={orderRef}";
    }
}