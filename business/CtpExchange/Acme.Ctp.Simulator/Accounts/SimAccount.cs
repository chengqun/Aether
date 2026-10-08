using System;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>账户汇总（管理面 <see cref="SimTradingCore.QueryAccounts"/> 的行）。</summary>
public sealed record SimAccountSummary(
    string AccountId,
    string? BrokerId,
    bool HasPassword,
    double Balance,
    double Available,
    int LongLots,
    int ShortLots,
    int LiveOrders);

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

    /// <summary>登录密码哈希（<c>pbkdf2$迭代$盐$哈希</c>；null = 未设——免认证，dev/测试路径）。</summary>
    public string? PasswordHash { get; internal set; }

    /// <summary>最近一次登录的经纪商代码（元数据——账户身份键仍是 <see cref="AccountId"/>，单一交易所内经纪商是形式字段）。</summary>
    public string? BrokerId { get; internal set; }

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

    /// <summary>
    /// 现货提币带走已付价款（桥烧毁路径——<b>不是平仓变现</b>）：占用保证金解除的<b>同时</b>等额扣减静态权益。
    /// 币已物理离所，对应的价款随币走（充币铸造的持仓 margin=0——本方法零效果，币白来的白走）。
    /// 净效果 <c>Available</c> 不变（保证金解除 + 权益扣减对冲）——区别于平仓（保证金解除 + 盈亏入权益）。
    /// </summary>
    public void ConsumeMarginForWithdrawal(double margin)
    {
        CurrMargin = Math.Max(0, CurrMargin - margin);
        StaticBalance -= Round(margin);
    }

    /// <summary>
    /// 计价币提现（批 6 双资产）：AQ 托管凭证离场——静态权益直接扣减（它是现金，不是保证金占用）。
    /// 闸门 = <b>静态可用</b>（StaticBalance − CurrMargin − Frozen——不含浮盈：浮盈是持仓盯市读数，
    /// 落袋前不是钱；锁定保证金 backing 挂单/持仓，动了会破坏资金守恒）。
    /// </summary>
    public bool TryWithdrawCash(double tokens)
    {
        if (StaticBalance - CurrMargin - FrozenMargin - FrozenCash < tokens - 1e-9) return false;
        StaticBalance -= Round(tokens);
        return true;
    }

    /// <summary>持仓标价（影响 <c>Balance</c>/<c>Available</c>）。</summary>
    public void SetPositionProfit(double value) => PositionProfit = value;

    /// <summary>按报价建仓时占用的保证金。</summary>
    public static double MarginOf(SimInstrument instrument, double price, int volume)
        => instrument.MarginPerLot(price) * volume;

    /// <summary>
    /// 手续费（成交额 × 费率）。isMaker：被动成交按 maker 费率（挂单便宜）、攻击成交按 taker 费率（吃单贵）
    /// ——BTC 对标（2026-10-05）；未配置差异化费率时两者同为 <see cref="SimInstrument.CommissionRate"/>（CTP 形态）。
    /// 下单冻结用 taker 版（最坏情形保守冻结——maker 成交实际计费更少，差额在终态/撤销释放）。
    /// </summary>
    public static double FeeOf(SimInstrument instrument, double price, int volume, bool isMaker = false)
        => Math.Abs(price) * instrument.VolumeMultiple * volume
           * (isMaker ? instrument.EffectiveMakerFeeRate : instrument.EffectiveTakerFeeRate);

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
        AccountId, StaticBalance, CurrMargin, FrozenMargin, FrozenCash, Commission, CloseProfit, PositionProfit,
        PasswordHash, BrokerId);

    /// <summary>从快照恢复。</summary>
    public static SimAccount FromSnapshot(Storage.SimAccountSnapshot s) => new(s.AccountId, s.StaticBalance)
    {
        CurrMargin = s.CurrMargin,
        FrozenMargin = s.FrozenMargin,
        FrozenCash = s.FrozenCash,
        Commission = s.Commission,
        CloseProfit = s.CloseProfit,
        PositionProfit = s.PositionProfit,
        PasswordHash = s.PasswordHash,
        BrokerId = s.BrokerId,
    };

    /// <summary>金额四舍五入到分（浮点累加误差不泄漏到 UI/落盘）。</summary>
    internal static double Round(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
