using Acme.Ctp.AI.Tools;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tests;

/// <summary>
/// CTP 查询工具（账户/持仓/行情）回归守卫：底层异常不再以裸堆栈透给 agent——
/// 工具返回结构化 Output（数据字段 / Error 单行二择一），镜像 DataQuery <c>ExecuteSqlOutput</c> 约定。
/// 修复前工具方法直接透传 service 调用，MEAI 工具异常会把整段堆栈塞进模型上下文；
/// <see cref="QueryMarketDataTool"/> 还把 null 合约号漏进 <c>_latestTicks.TryGetValue(null)</c>（ArgumentNullException）。
/// </summary>
public class QueryToolTests
{
    /// <summary>记录调用、可配置返回/抛出的假 service（镜像 OrderToolTests 的 FakeTradingService）。</summary>
    private sealed class FakeTradingService : ICtpTradingService
    {
        public Exception? ThrowOnQueryAccount { get; set; }
        public Exception? ThrowOnQueryPosition { get; set; }
        public Exception? ThrowOnGetTick { get; set; }
        public Exception? ThrowOnGetAllTicks { get; set; }
        public CtpTick? LatestTick { get; set; }
        public int GetLatestTickCalls { get; private set; }

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

        public Task<CtpAccount> QueryAccountAsync(CancellationToken ct = default)
            => ThrowOnQueryAccount is not null ? Task.FromException<CtpAccount>(ThrowOnQueryAccount)
               : Task.FromResult(new CtpAccount { AccountId = "9999", Balance = 100_000, Available = 80_000 });

        public Task<IReadOnlyList<CtpPosition>> QueryPositionAsync(string? instrumentId = null, CancellationToken ct = default)
            => ThrowOnQueryPosition is not null ? Task.FromException<IReadOnlyList<CtpPosition>>(ThrowOnQueryPosition)
               : Task.FromResult<IReadOnlyList<CtpPosition>>(new[] { new CtpPosition { InstrumentId = "rb2601", TotalPosition = 2 } });

        public Task<IReadOnlyList<CtpOrder>> QueryOrderAsync(string? instrumentId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CtpInstrument>> QueryInstrumentsAsync(CancellationToken ct = default) => throw new NotImplementedException();

        public Task<CtpOrderInsertResult> PlaceOrderAsync(CtpOrderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelOrderAsync(CtpOrderRef orderRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SubscribeMarketDataAsync(CtpMarketDataConnect connect, IReadOnlyList<string> symbols, CancellationToken ct = default) => Task.CompletedTask;
        public void UnsubscribeMarketData() { }

        public CtpTick? GetLatestTick(string instrumentId)
        {
            GetLatestTickCalls++;
            if (ThrowOnGetTick is not null) throw ThrowOnGetTick;
            return LatestTick;
        }

        public IReadOnlyDictionary<string, CtpTick> GetAllLatestTicks()
            => ThrowOnGetAllTicks is not null ? throw ThrowOnGetAllTicks
               : new Dictionary<string, CtpTick> { ["rb2601"] = new() { InstrumentId = "rb2601", LastPrice = 3500 } };
    }

    // —— QueryAccount ——

    [Fact]
    public async Task QueryAccount_returns_account_on_success()
    {
        var tool = new QueryAccountTool(new FakeTradingService());
        var output = await tool.QueryAccountAsync();
        Assert.Null(output.Error);
        Assert.NotNull(output.Account);
        Assert.Equal("9999", output.Account!.AccountId);
    }

    [Fact]
    public async Task QueryAccount_failure_returns_structured_error_without_raw_stack()
    {
        var tool = new QueryAccountTool(new FakeTradingService
        {
            ThrowOnQueryAccount = new InvalidOperationException("CTP 未登录"),
        });

        var output = await tool.QueryAccountAsync();

        Assert.Null(output.Account);
        Assert.Contains("查询资金账户失败", output.Error);
        Assert.Contains("CTP 未登录", output.Error);
        Assert.DoesNotContain("   at ", output.Error); // 无原始堆栈
    }

    [Fact]
    public async Task QueryAccount_cancellation_returns_cancelled_message()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = new QueryAccountTool(new FakeTradingService
        {
            ThrowOnQueryAccount = new OperationCanceledException(cts.Token),
        });

        var output = await tool.QueryAccountAsync(cts.Token);

        Assert.Null(output.Account);
        Assert.Contains("取消", output.Error);
    }

    // —— QueryPositions ——

    [Fact]
    public async Task QueryPositions_returns_positions_on_success()
    {
        var tool = new QueryPositionTool(new FakeTradingService());
        var output = await tool.QueryPositionsAsync();
        Assert.Null(output.Error);
        Assert.NotNull(output.Positions);
        Assert.Single(output.Positions!);
        Assert.Equal("rb2601", output.Positions![0].InstrumentId);
    }

    [Fact]
    public async Task QueryPositions_failure_returns_structured_error_without_raw_stack()
    {
        var tool = new QueryPositionTool(new FakeTradingService
        {
            ThrowOnQueryPosition = new TimeoutException("查询超时"),
        });

        var output = await tool.QueryPositionsAsync();

        Assert.Null(output.Positions);
        Assert.Contains("查询持仓失败", output.Error);
        Assert.Contains("查询超时", output.Error);
        Assert.DoesNotContain("   at ", output.Error);
    }

    // —— QueryLatestTick / QueryAllLatestTicks ——

    [Fact]
    public async Task QueryLatestTick_empty_instrument_returns_error_without_calling_service()
    {
        // 修复前 null/空合约号漏进底层 _latestTicks.TryGetValue(null) → ArgumentNullException 裸堆栈。
        var fake = new FakeTradingService();
        var tool = new QueryMarketDataTool(fake);

        var output = await tool.QueryLatestTickAsync("  ");

        Assert.Null(output.Tick);
        Assert.Contains("不能为空", output.Error);
        Assert.Equal(0, fake.GetLatestTickCalls); // 校验先于 service 调用
    }

    [Fact]
    public async Task QueryLatestTick_no_data_is_normal_state_not_error()
    {
        // 未订阅/尚无 tick 是正常态：Tick=null 且 Error=null（区别于底层异常）。
        var tool = new QueryMarketDataTool(new FakeTradingService { LatestTick = null });

        var output = await tool.QueryLatestTickAsync("rb2601");

        Assert.Null(output.Error);
        Assert.Null(output.Tick);
        Assert.Equal("rb2601", output.InstrumentId);
    }

    [Fact]
    public async Task QueryLatestTick_service_failure_returns_structured_error()
    {
        var tool = new QueryMarketDataTool(new FakeTradingService
        {
            ThrowOnGetTick = new InvalidOperationException("行情未订阅"),
        });

        var output = await tool.QueryLatestTickAsync("rb2601");

        Assert.Null(output.Tick);
        Assert.Contains("查询最新行情失败", output.Error);
        Assert.Contains("行情未订阅", output.Error);
    }

    [Fact]
    public async Task QueryAllLatestTicks_failure_returns_structured_error()
    {
        var tool = new QueryMarketDataTool(new FakeTradingService
        {
            ThrowOnGetAllTicks = new InvalidOperationException("行情通道断开"),
        });

        var output = await tool.QueryAllLatestTicksAsync();

        Assert.Null(output.Ticks);
        Assert.Contains("查询全部最新行情失败", output.Error);
        Assert.Contains("行情通道断开", output.Error);
    }
}
