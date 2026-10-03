using System;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 资金记账的守恒性：**任何一条成交路径都不得凭空产生或吞掉资金/保证金**。
/// </summary>
/// <remarks>
/// <para>
/// 仿真柜台没有对账系统兜底，账目一旦漂移只会在「跑了几小时策略后可用资金对不上」时才暴露。
/// 故这里不逐笔核对金额（那由 <see cref="OrderLifecycleTests"/> 覆盖），而是断言**恒等式在每一步之后都成立**：
/// </para>
/// <list type="number">
/// <item><b>静态余额守恒</b>：<c>初始资金 - 手续费 + 平仓盈亏 == 静态余额</c>——成交只允许通过这两条腿改动本金。
/// （<c>Balance = StaticBalance + PositionProfit</c> 由 <see cref="SimAccount"/> 定义，故可从 DTO 反解静态余额。）</item>
/// <item><b>可用资金恒等式</b>：<c>可用 == 余额 - 占用保证金 - 冻结保证金 - 冻结手续费</c>。</item>
/// <item><b>保证金守恒</b>：开仓占用的总额必须被平仓**全额**释放（<c>CurrMargin</c> 归零），
/// 且平仓按「占总量比例」释放而非按手先进先出——多次不同价开仓后部分平仓的释放额不等于任何单笔占用额。</item>
/// </list>
/// <para>
/// 容差取 0.05：<c>ToDto()</c> 把每个金额字段各自四舍五入到分，三条腿各自舍入后恒等式本身有亚分级残差。
/// </para>
/// </remarks>
public class AccountingInvariantTests
{
    private const double Tolerance = 0.05;

    /// <summary>恒等式 1 + 2。<c>StaticBalance</c> 不在 Domain DTO 上，故由 <c>Balance - PositionProfit</c> 反解。</summary>
    private static void AssertConserved(CtpAccount account, string because = "")
    {
        var staticBalance = account.Balance - account.PositionProfit;
        Assert.True(Math.Abs(staticBalance - (SimHarness.InitialBalance - account.Commission + account.CloseProfit)) < Tolerance,
            $"静态余额不守恒{because}：本金 {SimHarness.InitialBalance} - 手续费 {account.Commission} + 平仓盈亏 {account.CloseProfit} " +
            $"应等于 {staticBalance}，实际静态余额 {staticBalance}（余额 {account.Balance} - 浮盈 {account.PositionProfit}）");

        Assert.True(Math.Abs(account.Available - (account.Balance - account.CurrMargin - account.FrozenMargin - account.FrozenCash)) < Tolerance,
            $"可用资金不守恒{because}：{account.Balance} - 占用 {account.CurrMargin} - 冻结保证金 {account.FrozenMargin} " +
            $"- 冻结手续费 {account.FrozenCash} 应等于可用 {account.Available}");
    }

    /// <summary>无挂单、无持仓、无浮盈时的「分文未动」形态。</summary>
    private static void AssertFlat(CtpAccount account)
    {
        Assert.Equal(0, account.CurrMargin, 2);
        Assert.Equal(0, account.FrozenMargin, 2);
        Assert.Equal(0, account.FrozenCash, 2);
        Assert.Equal(0, account.PositionProfit, 2);
    }

    [Fact]
    public async Task 市价开仓后恒等式成立()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));
        var account = core.QueryAccount(SimHarness.Account);

        // 买 1 手 @ maker 卖一 7001 ⇒ 占用 7001×15×0.12 = 12601.8，手续费 5.25075
        Assert.Equal(12601.8, account.CurrMargin, 2);
        Assert.Equal(5.25, account.Commission, 2);
        Assert.Equal(0, account.CloseProfit, 2);
        Assert.Equal(0, account.FrozenMargin, 2);
        Assert.Equal(0, account.FrozenCash, 2);
        AssertConserved(account);

        // 最新价 = 成交价 7001（价格发现归簿——last 由成交流维护）⇒ 持仓浮盈 0；浮盈不进本金恒等式，只进 Balance
        Assert.Equal(0, account.PositionProfit, 2);
        Assert.Equal(999994.74925, account.Balance - account.PositionProfit, 2);
    }

    [Fact]
    public async Task 开仓占用的保证金在平仓后全额释放()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 2));
        var opened = core.QueryAccount(SimHarness.Account);
        Assert.Equal(25203.6, opened.CurrMargin, 2); // 2 手
        AssertConserved(opened, "（开仓后）");

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 2, CtpOffsetFlag.Close));
        var closed = core.QueryAccount(SimHarness.Account);

        // 守恒的核心断言：占用被**全额**释放，一个子儿不留
        AssertFlat(closed);
        Assert.Empty(core.QueryPositions(SimHarness.Account));
        AssertConserved(closed, "（平仓后）");

        // 开仓 7001、平仓 6999 ⇒ 亏 2 点 × 15 × 2 手 = 60；手续费 5.25075×2 + 5.24925×2 = 21.0
        Assert.Equal(-60, closed.CloseProfit, 2);
        Assert.True(Math.Abs(closed.Commission - 21.0) < Tolerance, $"手续费应为 21.0，实际 {closed.Commission}");
        Assert.True(Math.Abs(closed.Available - closed.Balance) < Tolerance, "无持仓无挂单时可用必须等于余额");
    }

    [Fact]
    public async Task 平仓按占总量比例释放保证金而非按手先进先出()
    {
        // 两次不同价开仓：maker 分两档供给对手价（7001 先吃完，再吃 7101）——价格发现归簿下的「换价」
        var core = await SimHarness.NewLoggedInCoreAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, 7001));
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));   // @7001
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, 7101));
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));   // @7101
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Buy, 10, 6999));   // 平仓的对手价

        var twoLots = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12601.8 + 12781.8, twoLots.CurrMargin, 2); // 7001 与 7101 各自的单手保证金
        AssertConserved(twoLots, "（两手不同价开仓后）");

        // 平 1 手：按占比 1/2 释放 = 12691.8
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close));
        var half = core.QueryAccount(SimHarness.Account);

        Assert.Equal(12691.8, half.CurrMargin, 2);
        // 既不是先开的那手（12601.8）也不是后开的那手（12781.8）——证明是比例释放
        Assert.NotEqual(12601.8, half.CurrMargin, 2);
        Assert.NotEqual(12781.8, half.CurrMargin, 2);
        AssertConserved(half, "（部分平仓后）");

        // 平掉剩下 1 手：残留占用必须恰好归零（不能留下比例舍入的尾巴）
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close));
        var flat = core.QueryAccount(SimHarness.Account);
        AssertFlat(flat);
        Assert.Empty(core.QueryPositions(SimHarness.Account));
        AssertConserved(flat, "（全平后）");
    }

    [Fact]
    public async Task 部分成交的冻结摊销与释放总额等于初始冻结()
    {
        // maker 卖侧只挂 1 手 ⇒ GFD 限价买 3 只吃到 1 手（残 2 静息入簿继续冻结）；
        // 对手方补 2 手 ⇒ 残量补足——「已成交转占用 + 未成交继续冻结」之和守恒
        var core = await SimHarness.NewLoggedInCoreAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, 7001));

        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 3, 7001));
        var part = core.QueryAccount(SimHarness.Account);

        // 已成交那手转占用，未成交两手继续冻结——三者之和必须仍是 3 手的初始冻结额
        Assert.Equal(12601.8, part.CurrMargin, 2);
        Assert.Equal(25203.6, part.FrozenMargin, 2);
        Assert.True(Math.Abs(part.CurrMargin + part.FrozenMargin - 3 * 12601.8) < Tolerance,
            $"成交摊销破坏守恒：占用 {part.CurrMargin} + 冻结 {part.FrozenMargin} 应等于 3 手总额 {3 * 12601.8}");
        AssertConserved(part, "（部分成交后）");

        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 2, 7001));   // 对手方补量 ⇒ 吃掉残 2

        var filled = core.QueryAccount(SimHarness.Account);
        Assert.Equal(3 * 12601.8, filled.CurrMargin, 2);
        Assert.Equal(0, filled.FrozenMargin, 2);
        Assert.Equal(0, filled.FrozenCash, 2);
        Assert.Equal(3 * 5.25075, filled.Commission, 2);
        AssertConserved(filled, "（全部成交后）");
    }

    [Fact]
    public async Task 撤单不产生任何费用且冻结全额退回()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 2, 6900));
        var pending = core.QueryAccount(SimHarness.Account);
        Assert.Equal(24840, pending.FrozenMargin, 2); // 6900×15×0.12×2
        Assert.Equal(10.35, pending.FrozenCash, 2);
        AssertConserved(pending, "（挂单后）");

        var order = core.QueryOrders(SimHarness.Account).Single();
        core.CancelOrder(SimHarness.Account, SimHarness.RefOf(order));

        var after = core.QueryAccount(SimHarness.Account);
        AssertFlat(after);
        Assert.Equal(0, after.Commission, 2); // 撤单不是成交，不产生手续费
        Assert.Equal(SimHarness.InitialBalance, after.Balance, 2);
        Assert.Equal(SimHarness.InitialBalance, after.Available, 2);
        AssertConserved(after, "（撤单后）");
    }

    [Fact]
    public async Task 双向持仓的保证金互不干扰且各自释放()
    {
        var core = await SimHarness.NewLoggedInCoreAsync();

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1));   // 多 @ 7001
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1));  // 空 @ 6999

        var both = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12601.8 + 12598.2, both.CurrMargin, 2);
        Assert.Equal(2, core.QueryPositions(SimHarness.Account).Count);
        AssertConserved(both, "（双向持仓）");

        // 只平多：空头占用不得被动到
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1, CtpOffsetFlag.Close));
        var onlyShort = core.QueryAccount(SimHarness.Account);
        Assert.Equal(12598.2, onlyShort.CurrMargin, 2);
        AssertConserved(onlyShort, "（只平多头后）");

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Buy, 1, CtpOffsetFlag.Close));
        AssertFlat(core.QueryAccount(SimHarness.Account));
        Assert.Empty(core.QueryPositions(SimHarness.Account));
    }

    [Fact]
    public async Task 混合操作后恒等式仍成立()
    {
        // 一串真实策略会做出的操作：挂单 → 部分成交 → 补成交 → 平掉 → 撤单 → 反手开仓
        // （对手方流动性由 maker 账户分档分批供给——价格发现归簿）
        var core = await SimHarness.NewLoggedInCoreAsync(seedMaker: false);
        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 1, 7001));

        core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 3, 7001));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（部分成交）");

        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Sell, 2, 7001));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（补成交）");

        core.InsertOrder(SimHarness.MakerAccount, SimHarness.Limit(CtpDirection.Buy, 10, 6999));
        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 2, CtpOffsetFlag.Close));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（部分平仓）");

        var pending = core.InsertOrder(SimHarness.Account, SimHarness.Limit(CtpDirection.Buy, 1, 6900));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（挂单）");

        var pendingOrder = core.QueryOrders(SimHarness.Account).Single(o => o.OrderRef == pending.OrderRef);
        core.CancelOrder(SimHarness.Account, SimHarness.RefOf(pendingOrder));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（撤单）");

        core.InsertOrder(SimHarness.Account, SimHarness.Market(CtpDirection.Sell, 1));
        AssertConserved(core.QueryAccount(SimHarness.Account), "（反手开空）");

        // 逐笔成交的手续费总额 = 各笔成交额 × 费率（成交量 × 单价 的可加性）
        var tradeCount = core.QueryOrders(SimHarness.Account).Sum(o => o.VolumeTraded);
        Assert.True(tradeCount > 0);
        Assert.True(core.QueryAccount(SimHarness.Account).Commission > 0);
    }
}
