using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

/// <summary>
/// CTP 报单工具。审批闸门在**工具注册层**（<c>AddCtpTools</c> 标 <see cref="ToolApprovalLevel.Dangerous"/> →
/// 由聊天模式选择器/审批对话框放行），故此处不再用默认开启的二次确认把工具变成 no-op。
/// <paramref name="dryRun"/> 仅作 agent 主动的「先报价、等用户点头」预览开关，默认 false = 调用即下单。
/// </summary>
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
        double limitPrice, int volume,
        [Description("true = 只返回订单预览文本、不真正报单（供先向用户确认的场景）；false/缺省 = 直接报单。注意：真正的安全闸门是本工具的审批等级，不是此参数。")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (dryRun)
            return $"订单预览（dryRun，未报单）: {Describe(direction, offsetFlag, instrumentId, volume, priceType, limitPrice)}";

        try
        {
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
            }, cancellationToken);

            return $"已下单: OrderRef={result.OrderRef}";
        }
        catch (OperationCanceledException)
        {
            return "已取消下单（调用被取消）。";
        }
        catch (Exception ex)
        {
            // 不要把原始堆栈透给 agent——返回结构化单行错误，便于其判断重试/换参。
            return $"下单失败: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string Describe(CtpDirection direction, CtpOffsetFlag offsetFlag,
        string instrumentId, int volume, CtpOrderPriceType priceType, double limitPrice)
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
        return $"{dirStr}{offsetStr} {instrumentId} {volume}手 {priceStr}";
    }
}
