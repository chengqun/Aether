using System;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 资金账户（仿真账本口径，逐笔精确）。
/// <para>
/// 口径定义（写在此处以免各处重算）：
/// <list type="bullet">
/// <item><c>StaticBalance</c> = 入金 + 平仓盈亏 − 手续费（**已实现**部分的权益）。</item>
/// <item><c>Balance</c>（动态权益）= <c>StaticBalance</c> + <c>PositionProfit</c>（持仓浮盈浮亏）。</item>
/// <item><c>Available</c> = <c>Balance</c> − <c>CurrMargin</c> − <c>FrozenMargin</c> − <c>FrozenCash</c>。</item>
/// </list>
/// </para>
/// <para>
/// <b>冻结 → 消耗/退还</b>是两条不同的路径，混用会让权益多算：
/// 下单冻结（<see cref="FreezeForOrder"/>）后，成交把对应份额**消耗**掉
/// （<see cref="ConsumeFrozenMargin"/> 转持仓占用 / <see cref="ConsumeFrozenFee"/> 落已实现），
/// 撤单或 IOC·FOK 残量作废则把份额**退还**（<see cref="ReleaseForOrder"/>，直接回可用）。
/// </para>
/// <para>
/// 与真柜台的差异：真柜台 <c>Balance</c> 含逐日盯市结算后的持仓盈亏，手续费按成交当日扣。
/// 仿真不做日结算（<c>PreSettlement</c> 只作涨跌停基准），故手续费在**成交时**从 <c>StaticBalance</c> 扣除。
/// </para>
/// </summary>
public sealed class SimAccount
{
    public SimAccount(string accountId, double initialBalance)
    {
        AccountId = accountId;
        StaticBalance = initialBalance;
    }

    public string AccountId { get; }

    /// <summary>静态权益（入金 + 平仓盈亏 − 手续费）。</summary>
    public double StaticBalance { get; private set; }

    /// <summary>持仓占用保证金（已成交部分）。</summary>
    public double CurrMargin { get; private set; }

    /// <summary>挂单冻结保证金（未成交部分的预扣）。平仓单不冻结保证金（平仓是减仓，不占新保证金）。</summary>
    public double FrozenMargin { get; private set; }

    /// <summary>挂单冻结手续费（未成交部分的预扣）。</summary>
    public double FrozenCash { get; private set; }

    /// <summary>累计手续费。</summary>
    public double Commission { get; private set; }

    /// <summary>累计平仓盈亏。</summary>
    public double CloseProfit { get; private set; }

    /// <summary>持仓浮动盈亏（由持仓管理器按最新价写）。</summary>
    public double PositionProfit { get; private set; }

    /// <summary>动态权益。</summary>
    public double Balance => StaticBalance + PositionProfit;

    /// <summary>可用资金。</summary>
    public double Available => Balance - CurrMargin - FrozenMargin - FrozenCash;

    /// <summary>可取资金（仿真等同可用）。</summary>
    public double WithdrawQuota => Available;

    /// <summary>入金。</summary>
    public void Deposit(double amount) => StaticBalance += amount;

    /// <summary>下单被接受：冻结该单的保证金 + 手续费。</summary>
    public void FreezeForOrder(double margin, double fee)
    {
        FrozenMargin += margin;
        FrozenCash += fee;
    }

    /// <summary>撤单 / 残量作废：退还冻结（回可用资金，不进已实现）。</summary>
    public void ReleaseForOrder(double margin, double fee)
    {
        FrozenMargin = Math.Max(0, FrozenMargin - margin);
        FrozenCash = Math.Max(0, FrozenCash - fee);
    }

    /// <summary>成交消耗冻结保证金 → 转为持仓占用保证金。</summary>
    public void ConsumeFrozenMargin(double margin)
    {
        FrozenMargin = Math.Max(0, FrozenMargin - margin);
        CurrMargin += margin;
    }

    /// <summary>成交消耗冻结手续费 → 落为已实现手续费。</summary>
    public void ConsumeFrozenFee(double fee)
    {
        FrozenCash = Math.Max(0, FrozenCash - fee);
        Commission += fee;
        StaticBalance -= fee;
    }

    /// <summary>平仓成交：释放该部分持仓的占用保证金（由持仓管理器按比例算出）。</summary>
    public void ReleasePositionMargin(double margin)
        => CurrMargin = Math.Max(0, CurrMargin - margin);

    /// <summary>平仓成交：平仓盈亏入已实现权益。</summary>
    public void ApplyClosedPnl(double pnl)
    {
        CloseProfit += pnl;
        StaticBalance += pnl;
    }

    /// <summary>持仓标价（影响 <c>Balance</c>/<c>Available</c>）。</summary>
    public void SetPositionProfit(double value) => PositionProfit = value;

    /// <summary>按报价建仓时占用的保证金。</summary>
    public static double MarginOf(SimInstrument instrument, double price, int volume)
        => instrument.MarginPerLot(price) * volume;

    /// <summary>手续费（成交额 × 费率）。</summary>
    public static double FeeOf(SimInstrument instrument, double price, int volume)
        => Math.Abs(price) * instrument.VolumeMultiple * volume * instrument.CommissionRate;

    /// <summary>镜像成 Domain DTO（UI / AI 工具消费）。</summary>
    public CtpAccount ToDto() => new()
    {
        AccountId = AccountId,
        Balance = Round(Balance),
        Available = Round(Available),
        WithdrawQuota = Round(WithdrawQuota),
        FrozenMargin = Round(FrozenMargin),
        FrozenCash = Round(FrozenCash),
        Commission = Round(Commission),
        CurrMargin = Round(CurrMargin),
        PositionProfit = Round(PositionProfit),
        CloseProfit = Round(CloseProfit),
    };

    /// <summary>落盘快照。</summary>
    public Storage.SimAccountSnapshot ToSnapshot() => new(
        AccountId, StaticBalance, CurrMargin, FrozenMargin, FrozenCash, Commission, CloseProfit, PositionProfit);

    /// <summary>从快照恢复。</summary>
    public static SimAccount FromSnapshot(Storage.SimAccountSnapshot s) => new(s.AccountId, s.StaticBalance)
    {
        CurrMargin = s.CurrMargin,
        FrozenMargin = s.FrozenMargin,
        FrozenCash = s.FrozenCash,
        Commission = s.Commission,
        CloseProfit = s.CloseProfit,
        PositionProfit = s.PositionProfit,
    };

    /// <summary>金额四舍五入到分（浮点累加误差不泄漏到 UI/落盘）。</summary>
    internal static double Round(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
