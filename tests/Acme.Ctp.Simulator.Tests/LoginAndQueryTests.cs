using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 登录与查询：登录应答必须满足 <c>CtpTrader.LoginAsync</c> 的隐含契约
/// （<c>MaxOrderRef</c> 可被 <c>int.Parse</c> 解析），查询必须完整返回账本内容。
/// </summary>
public class LoginAndQueryTests
{
    [Fact]
    public async Task 登录应答的MaxOrderRef可被int解析()
    {
        var core = await SimHarness.NewCoreAsync();

        var login = SimHarness.Login(core);

        // CtpTrader.LoginAsync 拿它做起始单号（int.Parse）——不可解析会导致登录后第一步就崩
        Assert.True(int.TryParse(login.MaxOrderRef, out var maxRef), $"MaxOrderRef 必须是整数字符串，实际「{login.MaxOrderRef}」");
        Assert.Equal(0, maxRef); // 全新柜台：尚未分配过任何单号
        Assert.Equal(1, login.FrontId);
        Assert.Equal(1, login.SessionId);
        Assert.Equal("20260923", login.TradingDay);
        Assert.Equal("10:00:00", login.LoginTime);
    }

    [Fact]
    public async Task 首次登录发放初始资金()
    {
        var core = await SimHarness.NewCoreAsync();
        SimHarness.Login(core); // 账户在首次登录时 GetOrCreate（初始资金是账户创建的产物）

        var account = core.QueryAccount(SimHarness.Account);

        Assert.Equal(SimHarness.Account, account.AccountId);
        Assert.Equal(SimHarness.InitialBalance, account.Balance, 2);
        Assert.Equal(SimHarness.InitialBalance, account.Available, 2);
        Assert.Equal(0, account.FrozenMargin, 2);
        Assert.Equal(0, account.CurrMargin, 2);
        Assert.Equal(0, account.Commission, 2);
    }

    [Fact]
    public async Task 未登录的账号查询返回空账户与空列表()
    {
        var core = await SimHarness.NewCoreAsync();

        var account = core.QueryAccount("nobody");

        Assert.Equal("nobody", account.AccountId);
        Assert.Equal(0, account.Balance, 2);
        Assert.Empty(core.QueryOrders("nobody"));
        Assert.Empty(core.QueryPositions("nobody"));
    }

    [Fact]
    public async Task 登录后合约表已播种()
    {
        var core = await SimHarness.NewCoreAsync();

        var instruments = core.QueryInstruments();

        Assert.Contains(instruments, i => i.InstrumentId == SimHarness.Instrument);
        Assert.Single(instruments, i => i.InstrumentId == SimHarness.Instrument);
        var ag = instruments.Single(i => i.InstrumentId == SimHarness.Instrument);
        Assert.Equal(SimHarness.VolumeMultiple, ag.VolumeMultiple);
        Assert.Equal(SimHarness.MarginRate, ag.MarginRate, 6);
        Assert.Equal(1, ag.PriceTick);
        Assert.Equal(6990, ag.PreSettlement, 2);
    }

    [Fact]
    public async Task 查询完整返回全部报单()
    {
        // 「多行查询」在 ICtpSession 接缝上是「列表长度 = 报单数」，且每笔单号互异 ——
        // 回归 SDK 侧 isLast=true 之后仍到达的行被静默丢弃的旧问题（表现在客户端而非核心）。
        var core = await SimHarness.NewLoggedInCoreAsync();

        var refs = new List<string>();
        for (var i = 0; i < 5; i++)
            refs.Add(core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900 - i)).OrderRef);

        var orders = core.QueryOrders(SimHarness.Account);

        Assert.Equal(5, orders.Count);
        Assert.Equal(refs, orders.Select(o => o.OrderRef).ToList());
        Assert.Equal(5, orders.Select(o => o.OrderRef).Distinct(StringComparer.Ordinal).Count());
        Assert.All(orders, o => Assert.Equal(SimHarness.Instrument, o.InstrumentId));

        // 按合约过滤
        Assert.Equal(5, core.QueryOrders(SimHarness.Account, SimHarness.Instrument).Count);
        Assert.Empty(core.QueryOrders(SimHarness.Account, "cu2610"));
    }

    [Fact]
    public async Task 查询持仓反映成交后的净持仓()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 2));

        var positions = core.QueryPositions(SimHarness.Account);

        var long_ = Assert.Single(positions);
        Assert.Equal(SimHarness.Instrument, long_.InstrumentId);
        Assert.Equal(CtpPosiDirection.Long, long_.PosiDirection);
        Assert.Equal(2, long_.TotalPosition);
        Assert.Equal(2, long_.TodayPosition);
        Assert.Equal(0, long_.YdPosition);

        Assert.Empty(core.QueryPositions(SimHarness.Account, "cu2610"));
        Assert.Single(core.QueryPositions(SimHarness.Account, SimHarness.Instrument));
    }

    [Fact]
    public async Task 下单回显单号且请求号可解析()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        var first = core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));
        var second = core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6901));

        Assert.Equal("000000000001", first.OrderRef);
        Assert.Equal(1, first.RequestId);
        Assert.Equal("000000000002", second.OrderRef);
        Assert.Equal(2, second.RequestId);
        Assert.Equal(first.RequestId, first.OrderRef.Length == 12 ? int.Parse(first.OrderRef) : -1);
    }

    [Fact]
    public void 账号编号归一规则与登录缓存一致()
    {
        Assert.Equal("tester", SimTradingCore.AccountIdOf("tester"));
        Assert.Equal("sim", SimTradingCore.AccountIdOf(null));
        Assert.Equal("sim", SimTradingCore.AccountIdOf("   "));
    }
}
