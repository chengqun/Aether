using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Acme.StockTrading.Domain;

namespace Acme.AppAutomation.StockTradingBridge;

/// <summary>
/// <see cref="IStockBrokerage"/> 的 MCP 实现（固定过程路径）。把每个经纪网关操作映射到
/// <see cref="IProcedureRunner"/> 里的同花顺固定过程（ths_connect/ths_place_order/…）。
/// <para>这是「多实现一个 MCP 的 <see cref="IStockBrokerage"/>」——与既有 FlaUI 进程内实现并列，
/// 现状不变；宿主在 Windows/Mac 侧按需注册本类（替换 FlaUI/桩）。本类不引 FlaUI、不碰 OS。</para>
/// <para>由 <c>AppAutomation.StockTradingBridge</c> 独立项目承载，保持 AppAutomation 通用、StockTrading 零改动。</para>
/// </summary>
public sealed class AppAutomationStockBrokerage : IStockBrokerage
{
    private readonly IProcedureRunner _runner;

    public AppAutomationStockBrokerage(IProcedureRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public bool IsConnected { get; private set; }
    public event EventHandler<string>? LogReceived;

    private void Emit(string msg) => LogReceived?.Invoke(this, msg);

    public async Task<(bool Success, string? Error)> ConnectAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("ths_connect", null, ct).ConfigureAwait(false);
        IsConnected = r.Success;
        Emit(r.Success ? "已连接（同花顺窗口已确认）" : r.Error ?? "连接失败");
        return (r.Success, r.Error);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _runner.RunAsync("ths_disconnect", null, ct).ConfigureAwait(false);
        IsConnected = false;
        Emit("已断开");
    }

    public async Task<(bool Success, string? Error)> LaunchClientAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("ths_launch", null, ct).ConfigureAwait(false);
        Emit(r.Success ? "客户端已启动" : r.Error ?? "启动失败");
        return (r.Success, r.Error);
    }

    public async Task<(bool Success, string? Error)> PlaceOrderAsync(StockOrderRequest request, CancellationToken ct = default)
    {
        var args = new ThsPlaceOrderArgs(
            request.Symbol,
            request.LimitPrice,
            request.Quantity,
            request.Direction == StockDirection.Sell ? "sell" : "buy");

        var r = await _runner.RunAsync("ths_place_order", args, ct).ConfigureAwait(false);
        Emit(r.Success
            ? $"下单已提交：{request.Direction} {request.Symbol} ×{request.Quantity} @ {request.LimitPrice}"
            : r.Error ?? "下单失败");
        return (r.Success, r.Error);
    }

    public async Task<(bool Success, string? Error)> CancelOrderAsync(string orderNo, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("ths_cancel", new ThsCancelArgs(orderNo), ct).ConfigureAwait(false);
        Emit(r.Success ? $"撤单已提交：{orderNo}" : r.Error ?? "撤单失败");
        return (r.Success, r.Error);
    }

    public async Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync("ths_query_positions", null, ct).ConfigureAwait(false);
        if (!r.Success || string.IsNullOrEmpty(r.Data))
        {
            Emit(r.Error ?? "无持仓数据");
            return Array.Empty<StockPosition>();
        }
        // TODO(Windows): 持仓表 UI 树结构确定后，把 r.Data 解析成 StockPosition[]。
        // 现骨架返回空（真实解析留到 Windows 期探明同花顺持仓表结构后实现）。
        Emit("持仓查询完成（解析待 Windows 期实现）");
        return Array.Empty<StockPosition>();
    }
}
