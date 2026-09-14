using Acme.Ctp.AI.Tools;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tests;

public class OrderToolTests
{
    /// <summary>记录调用、可配置返回/抛出的假 service。</summary>
    private sealed class FakeTradingService : ICtpTradingService
    {
        public bool PlaceCalled { get; private set; }
        public bool CancelCalled { get; private set; }
        public Exception? ThrowOnPlace { get; set; }
        public Exception? ThrowOnCancel { get; set; }

        public bool IsConnected => true;
        public string? BrokerId => "9999";
        public string? UserId => "tester";
        public CtpLoginResult? LoginResult => null;
        public bool IsMdSubscribed => false;

        public event EventHandler<CtpOrder>? OrderReceived;
        public event EventHandler<CtpTrade>? TradeReceived;
        public event EventHandler<CtpConnectionStatus>? ConnectionStateChanged;
        public event EventHandler<CtpTick>? TickReceived;

        public Task<CtpLoginResult> LoginAsync(CtpLoginRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default)
        {
            if (ThrowOnPlace is not null) throw ThrowOnPlace;
            PlaceCalled = true;
            return Task.FromResult(new CtpOrderInsertResult("42", 0));
        }

        public Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default)
        {
            if (ThrowOnCancel is not null) throw ThrowOnCancel;
            CancelCalled = true;
            return Task.CompletedTask;
        }

        public Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default) => Task.CompletedTask;
        public void UnsubscribeMarketData() { }
        public CtpTick? GetLatestTick(string instrumentId) => null;
        public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks() => new Dictionary<string, CtpTick>();
    }

    [Fact]
    public async Task OrderInsert_executes_by_default_without_requireConfirmation()
    {
        // 回归守卫：审批闸门在注册层（Dangerous），工具不再默认返回「待确认」no-op——
        // 旧实现 requireConfirmation 默认 true，用户通过审批后工具仍只回一句「待确认」而不下单。
        var svc = new FakeTradingService();
        var tool = new OrderInsertTool(svc);

        var result = await tool.OrderInsertAsync(
            "rb2601", CtpDirection.Buy, CtpOffsetFlag.Open, CtpOrderPriceType.Limit,
            limitPrice: 3500, volume: 1);

        Assert.True(svc.PlaceCalled, "OrderInsert should call PlaceOrderAsync by default");
        Assert.Contains("已下单", result);
        Assert.Contains("42", result);
    }

    [Fact]
    public async Task OrderInsert_dryRun_does_not_place()
    {
        var svc = new FakeTradingService();
        var tool = new OrderInsertTool(svc);

        var result = await tool.OrderInsertAsync(
            "rb2601", CtpDirection.Buy, CtpOffsetFlag.Open, CtpOrderPriceType.Limit,
            limitPrice: 3500, volume: 1, dryRun: true);

        Assert.False(svc.PlaceCalled);
        Assert.Contains("dryRun", result);
    }

    [Fact]
    public async Task OrderInsert_native_failure_returns_structured_message_not_raw_stack()
    {
        var svc = new FakeTradingService { ThrowOnPlace = new InvalidOperationException("CTP 未登录") };
        var tool = new OrderInsertTool(svc);

        var result = await tool.OrderInsertAsync(
            "rb2601", CtpDirection.Buy, CtpOffsetFlag.Open, CtpOrderPriceType.Limit,
            limitPrice: 3500, volume: 1);

        Assert.Contains("下单失败", result);
        Assert.Contains("CTP 未登录", result);
        Assert.DoesNotContain("   at ", result); // 无原始堆栈
    }

    [Fact]
    public async Task OrderAction_executes_by_default_without_requireConfirmation()
    {
        var svc = new FakeTradingService();
        var tool = new OrderActionTool(svc);

        var result = await tool.OrderActionAsync(1, 2, "100", "SHFE");

        Assert.True(svc.CancelCalled, "OrderAction should call CancelOrderAsync by default");
        Assert.Contains("已撤单", result);
    }

    [Fact]
    public async Task OrderAction_dryRun_does_not_cancel()
    {
        var svc = new FakeTradingService();
        var tool = new OrderActionTool(svc);

        var result = await tool.OrderActionAsync(1, 2, "100", "SHFE", dryRun: true);

        Assert.False(svc.CancelCalled);
        Assert.Contains("dryRun", result);
    }

    [Fact]
    public async Task OrderAction_native_failure_returns_structured_message()
    {
        var svc = new FakeTradingService { ThrowOnCancel = new InvalidOperationException("报单不存在") };
        var tool = new OrderActionTool(svc);

        var result = await tool.OrderActionAsync(1, 2, "100", "SHFE");

        Assert.Contains("撤单失败", result);
        Assert.Contains("报单不存在", result);
    }
}
