using System;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 风控拒单：拒单是**同步抛出** <see cref="CtpBusinessException"/>（真柜台的成功路径不返回错误码，
/// 异常是 <c>ICtpSession</c> 接缝上表达拒单的唯一通道），且拒单必须**不留任何痕迹**——
/// 不动资金、不建报单、不消耗单号。
/// </summary>
public class RiskRejectionTests
{
    /// <summary>断言下单被拒且错误码为 <paramref name="expectedErrorId"/>。</summary>
    private static CtpBusinessException Rejected(SimTradingCore core, CtpOrderRequest request)
    {
        var ex = Assert.Throws<CtpBusinessException>(() => core.InsertOrder(SimHarness.Account, request));
        Assert.False(string.IsNullOrWhiteSpace(ex.ErrorMsg), "拒单必须带可读原因（要原样透给客户端 UI）");
        return ex;
    }

    private static CtpBusinessException RejectedWith(SimTradingCore core, CtpOrderRequest request, int expectedErrorId)
    {
        var ex = Rejected(core, request);
        Assert.Equal(expectedErrorId, ex.ErrorId);
        return ex;
    }

    [Fact]
    public async Task 手数非正被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 0), SimErrorCodes.InvalidVolume);
        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, -3), SimErrorCodes.InvalidVolume);
    }

    [Fact]
    public async Task 限价非正被拒而市价不受此限()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        RejectedWith(core, SimHarness.Limit(CtpDirection.Buy, 1, 0), SimErrorCodes.InvalidPrice);
        RejectedWith(core, SimHarness.Limit(CtpDirection.Buy, 1, -1), SimErrorCodes.InvalidPrice);

        // 市价单的 LimitPrice 无意义（契约要求必填但会被忽略）——价格校验只针对限价单
        var market = SimHarness.Market(CtpDirection.Buy, 1);
        Assert.Equal(0, market.LimitPrice);
        Assert.Equal("000000000001", core.InsertOrder(SimHarness.Account, market).OrderRef);
    }

    [Fact]
    public async Task 未知合约被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync(
            new SimOptions { TickIntervalMs = 0, DatabasePath = null, AcceptUnknownInstruments = false });

        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 1, instrument: "zz9999"),
            SimErrorCodes.UnknownInstrument);

        // 空合约代码同样归入「未知合约」
        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 1, instrument: ""),
            SimErrorCodes.UnknownInstrument);
    }

    [Fact]
    public async Task 未知合约在兜底开启时被合成而非拒绝()
    {
        var core = await SimHarness.NewLoggedInCoreAsync(); // AcceptUnknownInstruments 默认 true

        var inserted = core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1, instrument: "zz9999"));

        // 合成合约可正常撮合（用默认乘数/保证金率）——这是「随便填个代码也能跑通流程」的能力来源
        Assert.Equal("000000000001", inserted.OrderRef);
        var synthetic = Assert.Single(core.QueryInstruments(), i => i.InstrumentId == "zz9999");
        Assert.Equal(InstrumentCatalog.DefaultUnknownVolumeMultiple, synthetic.VolumeMultiple);
        Assert.True(core.QueryAccount(SimHarness.Account).CurrMargin > 0, "合成合约的开仓应产生保证金占用");
    }

    [Fact]
    public async Task 资金不足被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        // 单手占用 ≈ 12601.8，可用 100 万 ⇒ 100 手需 126 万，必超
        var ex = RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 100), SimErrorCodes.InsufficientFunds);

        // 资金不足的原因串是「需保证金 1260180.00 + 手续费 …，可用 …。」
        // 拒单原因必须把「需多少 / 有多少」写清楚——用户靠这句话判断该填多少手
        Assert.Contains("资金不足", ex.ErrorMsg);
        Assert.Contains("1260180", ex.ErrorMsg);

        // 边界：恰好把可用资金用尽的一笔应当放行（1e-6 容差，防浮点把可成交的单判死）
        var affordable = core.QueryAccount(SimHarness.Account).Available;
        var lots = (int)Math.Floor(affordable / 12601.8);
        Assert.True(core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, lots, 7001)).OrderRef.Length > 0);
    }

    [Fact]
    public async Task 无可平持仓时平仓被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        RejectedWith(core, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close),
            SimErrorCodes.NoPositionToClose);
        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 1, CtpOffsetFlag.Close),
            SimErrorCodes.NoPositionToClose);
    }

    [Fact]
    public async Task 平仓手数超过可平量被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));

        var ex = RejectedWith(core, SimHarness.Market(CtpDirection.Sell, 2, CtpOffsetFlag.Close),
            SimErrorCodes.CloseVolumeExceeds);
        Assert.Contains("可平", ex.ErrorMsg);

        // 恰好 1 手可平 ⇒ 放行
        Assert.NotNull(core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close)));
        Assert.Empty(core.QueryPositions(SimHarness.Account));
    }

    [Fact]
    public async Task 挂单占用的可平量不会被重复平仓()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 2));

        // 第一笔平仓单挂在 7100（不成交），占用 2 手可平量
        // 注意方向：卖单要**高于**买一才挂得住（买限价 6900 挂、卖限价 6900 会立即吃掉买一 6999）
        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Sell, 2, 7100, CtpOffsetFlag.Close));

        // 第二笔平仓单：名义上还有 2 手持仓，但已被挂单全部占用 ⇒ 拒
        RejectedWith(core, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close),
            SimErrorCodes.NoPositionToClose);
    }

    [Fact]
    public async Task 平仓单只冻手续费不冻保证金()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
        var opened = core.QueryAccount(SimHarness.Account).CurrMargin;

        // 挂一笔不成交的平仓单（卖限价 7100 > 买一 6999 ⇒ 穿过不了，留在队列里）
        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Sell, 1, 7100, CtpOffsetFlag.Close));

        var pending = core.QueryAccount(SimHarness.Account);
        Assert.Equal(opened, pending.CurrMargin, 2);  // 平仓不新增占用
        Assert.Equal(0, pending.FrozenMargin, 2);     // 也冻结保证金（平仓是减仓）
        Assert.True(pending.FrozenCash > 0, "平仓单仍需冻结手续费");
    }

    [Fact]
    public async Task 拒单不消耗单号也不改账本()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 0), SimErrorCodes.InvalidVolume);
        RejectedWith(core, SimHarness.Market(CtpDirection.Buy, 100), SimErrorCodes.InsufficientFunds);

        // 单号仍从 1 起 —— 拒单发生在单号分配**之前**，否则客户端会看到跳号
        var inserted = core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
        Assert.Equal("000000000001", inserted.OrderRef);
        Assert.Equal(1, inserted.RequestId);

        Assert.Single(core.QueryOrders(SimHarness.Account));
        var account = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12601.8, account.CurrMargin, 2);
        Assert.Equal(0, account.FrozenMargin, 2);
    }

    [Fact]
    public async Task 撤不存在的报单被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        var bogus = new CtpOrderRef(SimHarness.Broker, SimHarness.Account, 1, 1, "999999999999", SimHarness.Exchange);
        var ex = Assert.Throws<CtpBusinessException>(() => core.CancelOrder(SimHarness.Account, bogus));
        Assert.Equal(SimErrorCodes.OrderNotFound, ex.ErrorId);
    }

    [Fact]
    public async Task 撤已终态的报单被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1)); // 立即全成交
        var done = core.QueryOrders(SimHarness.Account).Single();

        var ex = Assert.Throws<CtpBusinessException>(() => core.CancelOrder(SimHarness.Account, SimHarness.RefOf(done)));
        Assert.Equal(SimErrorCodes.OrderNotCancelable, ex.ErrorId);
        Assert.Contains("不可撤", ex.ErrorMsg);
    }

    [Fact]
    public async Task 未登录账号撤单被拒()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        var refOf = new CtpOrderRef(SimHarness.Broker, "nobody", 1, 1, "000000000001", SimHarness.Exchange);
        var ex = Assert.Throws<CtpBusinessException>(() => core.CancelOrder("nobody", refOf));
        Assert.Equal(SimErrorCodes.NotLoggedIn, ex.ErrorId);
    }

    [Fact]
    public async Task 时段闸门只在显式开启时生效()
    {
        // 默认（RejectOrdersOutsideTradingTime = false）：任意时刻都能下单——离线回放要的就是这个
        var lenient = await SimHarness.NewLoggedInCoreAsync(
            new SimOptions { TickIntervalMs = 0, DatabasePath = null },
            now: new DateTime(2026, 9, 26, 3, 0, 0)); // 周六凌晨
        Assert.NotNull(lenient.InsertOrder(SimHarness.Account,
            SimHarness.Market(CtpDirection.Buy, 1)));

        // 开启闸门：同一时刻被拒
        var strict = await SimHarness.NewLoggedInCoreAsync(
            new SimOptions { TickIntervalMs = 0, DatabasePath = null, RejectOrdersOutsideTradingTime = true },
            now: new DateTime(2026, 9, 26, 3, 0, 0));
        RejectedWith(strict, SimHarness.Market(CtpDirection.Buy, 1), SimErrorCodes.NotTradingTime);
    }
}
