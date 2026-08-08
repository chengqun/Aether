using System.Threading;
using System.Threading.Tasks;
using Acme.StockTrading.Application;
using Acme.StockTrading.Domain;

namespace Acme.StockTrading.AI.Tools;

/// <summary>股票交易连接/状态工具（只读，Safe）。</summary>
public class StockTradingStatusTool
{
    private readonly IStockTradingApplicationService _service;

    public StockTradingStatusTool(IStockTradingApplicationService service)
    {
        _service = service;
    }

    public Task<string> GetStatusAsync()
        => Task.FromResult(_service.IsConnected ? "已连接同花顺下单客户端" : "未连接");

    public async Task<string> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var (ok, err) = await _service.ConnectAsync(cancellationToken);
        return ok ? "已连接" : $"连接失败: {err}";
    }

    public async Task<string> LaunchClientAsync(CancellationToken cancellationToken = default)
    {
        var (ok, err) = await _service.LaunchClientAsync(cancellationToken);
        return ok ? "客户端已启动，请手动登录后再连接" : $"启动失败: {err}";
    }
}

/// <summary>持仓查询工具（只读，Safe）。</summary>
public class StockTradingPositionTool
{
    private readonly IStockTradingApplicationService _service;

    public StockTradingPositionTool(IStockTradingApplicationService service)
    {
        _service = service;
    }

    public async Task<IReadOnlyList<StockPosition>> QueryPositionsAsync(CancellationToken cancellationToken = default)
        => await _service.QueryPositionsAsync(cancellationToken);
}

/// <summary>股票下单/撤单工具（不可逆，Dangerous）。</summary>
public class StockTradingOrderTool
{
    private readonly IStockTradingApplicationService _service;

    public StockTradingOrderTool(IStockTradingApplicationService service)
    {
        _service = service;
    }

    public async Task<string> PlaceOrderAsync(
        string symbol,
        StockDirection direction,
        decimal limitPrice,
        int quantity,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "错误: 股票代码不能为空";
        if (quantity <= 0)
            return "错误: 数量必须大于 0";
        if (limitPrice <= 0)
            return "错误: 价格必须大于 0";

        var req = new StockOrderRequest
        {
            Symbol = symbol.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Direction = direction,
            LimitPrice = limitPrice,
            Quantity = quantity,
        };
        var (ok, err) = await _service.PlaceOrderAsync(req, cancellationToken);
        var dirStr = direction == StockDirection.Buy ? "买入" : "卖出";
        return ok
            ? $"已下单: {dirStr} {req.Symbol} {req.Quantity}股 @{req.LimitPrice}"
            : $"下单失败: {err}";
    }

    public async Task<string> CancelOrderAsync(string orderNo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderNo))
            return "错误: 单号不能为空";
        var (ok, err) = await _service.CancelOrderAsync(orderNo.Trim(), cancellationToken);
        return ok ? $"已撤单: {orderNo}" : $"撤单失败: {err}";
    }
}
