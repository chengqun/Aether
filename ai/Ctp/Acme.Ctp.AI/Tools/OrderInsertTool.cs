using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.AI.Tools;

public class OrderInsertTool
{
    private readonly CtpSessionService _session;

    public OrderInsertTool(CtpSessionService session)
    {
        _session = session;
    }

    public async Task<string> OrderInsertAsync(
        string instrumentId,
        Direction direction, OffsetFlag offsetFlag, OrderPriceType priceType,
        double limitPrice, int volume, bool requireConfirmation = true)
    {
        if (requireConfirmation)
        {
            var dirStr = direction == Direction.Buy ? "买" : "卖";
            var offsetStr = offsetFlag switch
            {
                OffsetFlag.Open => "开",
                OffsetFlag.Close => "平",
                OffsetFlag.CloseToday => "平今",
                _ => offsetFlag.ToString()
            };
            var priceStr = priceType == OrderPriceType.Limit ? $"@{limitPrice}" : "市价";
            return $"待确认: {dirStr}{offsetStr} {instrumentId} {volume}手 {priceStr}";
        }

        var result = await _session.OrderInsertAsync(new OrderRequest
        {
            BrokerId = _session.BrokerId ?? "",
            InvestorId = _session.UserId ?? "",
            InstrumentId = instrumentId,
            Direction = direction,
            OffsetFlag = offsetFlag,
            PriceType = priceType,
            LimitPrice = limitPrice,
            Volume = volume
        });

        return $"已下单: OrderRef={result.OrderRef}";
    }
}
