using Acme.AppAutomation.Domain;
using Acme.AppAutomation.Infrastructure;
using Acme.AppAutomation.StockTradingBridge;
using Acme.StockTrading.Domain;
using Xunit;

namespace Acme.AppAutomation.Tests;

/// <summary>记录用 <see cref="IProcedureRunner"/>：断言过程名 + 参数，返回可控结果。</summary>
public sealed class RecordingProcedureRunner : IProcedureRunner
{
    public List<(string Name, object? Args)> Calls { get; } = new();
    public ProcedureResult Return { get; set; } = ProcedureResult.Ok();

    public Task<ProcedureResult> RunAsync(string name, object? args = null, CancellationToken ct = default)
    {
        Calls.Add((name, args));
        return Task.FromResult(Return);
    }
}

public class AppAutomationStockBrokerageTests
{
    [Fact]
    public async Task PlaceOrder_maps_request_to_ths_place_order_with_direction()
    {
        var runner = new RecordingProcedureRunner();
        var brokerage = new AppAutomationStockBrokerage(runner);
        var req = new StockOrderRequest
        {
            Symbol = "600000",
            Direction = StockDirection.Sell,
            LimitPrice = 12.5m,
            Quantity = 100,
        };

        var (ok, _) = await brokerage.PlaceOrderAsync(req);

        Assert.True(ok);
        var (name, args) = Assert.Single(runner.Calls);
        Assert.Equal("ths_place_order", name);
        var pa = Assert.IsType<ThsPlaceOrderArgs>(args);
        Assert.Equal("600000", pa.Symbol);
        Assert.Equal(12.5m, pa.Price);
        Assert.Equal(100, pa.Quantity);
        Assert.Equal("sell", pa.Direction);
    }

    [Fact]
    public async Task Connect_sets_IsConnected_on_success()
    {
        var runner = new RecordingProcedureRunner { Return = ProcedureResult.Ok() };
        var brokerage = new AppAutomationStockBrokerage(runner);

        Assert.False(brokerage.IsConnected);
        var (ok, _) = await brokerage.ConnectAsync();

        Assert.True(ok);
        Assert.True(brokerage.IsConnected);
        Assert.Equal("ths_connect", runner.Calls[0].Name);
    }

    [Fact]
    public async Task Connect_failure_leaves_IsConnected_false()
    {
        var runner = new RecordingProcedureRunner { Return = ProcedureResult.Fail("未找到窗口") };
        var brokerage = new AppAutomationStockBrokerage(runner);

        var (ok, err) = await brokerage.ConnectAsync();

        Assert.False(ok);
        Assert.False(brokerage.IsConnected);
        Assert.Equal("未找到窗口", err);
    }

    [Fact]
    public async Task Cancel_maps_to_ths_cancel_with_order_no()
    {
        var runner = new RecordingProcedureRunner();
        var brokerage = new AppAutomationStockBrokerage(runner);

        await brokerage.CancelOrderAsync("12345");

        var (name, args) = Assert.Single(runner.Calls);
        Assert.Equal("ths_cancel", name);
        var ca = Assert.IsType<ThsCancelArgs>(args);
        Assert.Equal("12345", ca.OrderNo);
    }

    [Fact]
    public async Task QueryPositions_returns_empty_when_no_data()
    {
        var runner = new RecordingProcedureRunner { Return = ProcedureResult.Fail("无数据") };
        var brokerage = new AppAutomationStockBrokerage(runner);

        var positions = await brokerage.QueryPositionsAsync();

        Assert.Empty(positions);
        Assert.Equal("ths_query_positions", runner.Calls[0].Name);
    }
}
