using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

/// <summary>
/// CTP 撤单工具。审批闸门在**工具注册层**（<c>AddCtpTools</c> 标 <see cref="ToolApprovalLevel.Dangerous"/>），
/// 故此处不再用默认开启的二次确认把工具变成 no-op。<paramref name="dryRun"/> 仅作主动预览开关。
/// </summary>
public class OrderActionTool
{
    private readonly ICtpTradingService _service;

    public OrderActionTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<string> OrderActionAsync(
        int frontId, int sessionId, string orderRef, string exchangeId,
        [Description("true = 只返回撤单预览文本、不真正撤单；false/缺省 = 直接撤单。真正的安全闸门是本工具的审批等级。")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (dryRun)
            return $"撤单预览（dryRun，未撤单）: frontId={frontId} sessionId={sessionId} orderRef={orderRef} exchangeId={exchangeId}";

        try
        {
            await _service.CancelOrderAsync(new CtpOrderRef(
                _service.BrokerId ?? "", _service.UserId ?? "", frontId, sessionId, orderRef, exchangeId), cancellationToken);

            return $"已撤单: orderRef={orderRef}";
        }
        catch (OperationCanceledException)
        {
            return "已取消撤单（调用被取消）。";
        }
        catch (Exception ex)
        {
            return $"撤单失败: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
