using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class OrderInsertTool
{
    private readonly ICtpTradingService _service;

    public OrderInsertTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<string> OrderInsertAsync(
        string instrumentId,
        CtpDirection direction, CtpOffsetFlag offsetFlag, CtpOrderPriceType priceType,
        double limitPrice, int volume, bool requireConfirmation = true)
    {
        if (requireConfirmation)
        {
            var dirStr = direction == CtpDirection.Buy ? "买" : "卖";
            var offsetStr = offsetFlag switch
            {
                CtpOffsetFlag.Open => "开",
                CtpOffsetFlag.Close => "平",
                CtpOffsetFlag.CloseToday => "平今",
                _ => offsetFlag.ToString()
            };
            var priceStr = priceType == CtpOrderPriceType.Limit ? $"@{limitPrice}" : "市价";
            return $"待确认: {dirStr}{offsetStr} {instrumentId} {volume}手 {priceStr}";
        }

        var result = await _service.PlaceOrderAsync(new CtpOrderRequest
        {
            BrokerId = _service.BrokerId ?? "",
            InvestorId = _service.UserId ?? "",
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