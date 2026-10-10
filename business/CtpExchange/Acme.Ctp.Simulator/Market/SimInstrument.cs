using System;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 仿真柜台合约元数据 + 随机游走的起点。
/// <para>
/// 仓库内此前**不存在**任何合约静态数据（乘数/保证金率/最小变动价位），故仿真核心自带此表——
/// 真柜台的合约表由交易所下发，仿真的职责就是自己造一份能跑通撮合与保证金计算的最小集合。
/// 归属：仿真核心持有（<see cref="InstrumentCatalog"/> 种子 + <see cref="Storage.ISimStore"/> 落盘），
/// 不塞进 <c>Acme.Ctp.Domain</c>（Domain 是纯契约层，合约表是仿真实现细节）。
/// </para>
/// </summary>
public sealed record SimInstrument
{
    /// <summary>合约代码，如 <c>ag2610</c>。</summary>
    public required string InstrumentId { get; init; }

    /// <summary>交易所代码，如 <c>SHFE</c>（进 <c>CtpPosition.ExchangeId</c> / 撤单定位键）。</summary>
    public required string ExchangeId { get; init; }

    /// <summary>合约乘数（每点价值）。白银 ag=15、螺纹 rb=10、原油 sc=100…</summary>
    public double VolumeMultiple { get; init; } = 1;

    /// <summary>保证金率（交易所 + 期货公司加收的合计，仿真不再拆分）。</summary>
    public double MarginRate { get; init; } = 0.10;

    /// <summary>最小变动价位（随机游走步长与报价对齐都靠它）。</summary>
    public double PriceTick { get; init; } = 1;

    /// <summary>随机游走的初始中枢价。</summary>
    public double BasePrice { get; init; }

    /// <summary>昨结算（涨跌停板基准 + <c>CtpTick.PreSettlement</c>）。0 → 回退 <see cref="BasePrice"/>。</summary>
    public double PreSettlement { get; init; }

    /// <summary>手续费率（按成交额计，双边）。仿真的近似值，够测「手续费吃可用资金」。</summary>
    public double CommissionRate { get; init; } = 0.00005;

    /// <summary>maker（被动成交）手续费率；null = 回退 <see cref="CommissionRate"/>（CTP 双边同费形态）。
    /// BTC 对标（2026-10-05）：挂单便宜——被动成交按此计，费额 = 成交额 × 费率。</summary>
    public double? MakerCommissionRate { get; init; }

    /// <summary>taker（攻击成交）手续费率；null = 回退 <see cref="CommissionRate"/>。
    /// BTC 对标：吃单贵——攻击成交按此计；下单冻结亦按此（最坏情形保守冻结，maker 成交差额终态释放）。</summary>
    public double? TakerCommissionRate { get; init; }

    /// <summary>实际生效的 maker 费率（未配置回退单一费率）。</summary>
    public double EffectiveMakerFeeRate => MakerCommissionRate ?? CommissionRate;

    /// <summary>实际生效的 taker 费率（未配置回退单一费率）。</summary>
    public double EffectiveTakerFeeRate => TakerCommissionRate ?? CommissionRate;

    /// <summary>涨跌停板幅度（相对昨结算），默认 ±8%。随机游走与撮合价格都夹在此区间内。</summary>
    public double LimitRate { get; init; } = 0.08;

    /// <summary>
    /// 现货模式（批 4，2026-10-06；**2026-10-10 开空解禁**）：CTP 现货映射——双向持仓与期货一致
    /// （买=开多 / 卖=开空 / 平多平空）。MarginRate 配 1.0（100% = 全额占用：开多即「付款」、
    /// 开空全额保证金——MarginPerLot = price × VolumeMultiple × 1.0 = 完整名义价值）。
    /// 充币铸造（CreditSpotPosition）与提币烧毁（BurnSpotPosition，只烧 Long）仍是链上资产进出的唯一闸门。
    /// </summary>
    public bool IsSpot { get; init; }

    /// <summary>
    /// 现货供给上限（手数——总多头 OI ≤ 此值）。null = 不限（期货语义）。
    /// **可变**（get/set）——桥在 custody 链上余额变化时更新（同进程写、核心 `_gate` 内读——
    /// 良性竞态窗口：deposit 到位前一单最多多挂一手，下一单即恢复约束）。
    /// 非现货仪器忽略此字段。
    /// </summary>
    public int? SupplyLimitLots { get; set; }

    /// <summary>单手保证金。</summary>
    public double MarginPerLot(double price) => price * VolumeMultiple * MarginRate;

    /// <summary>把价格夹到涨跌停板区间（以昨结算为基准；无昨结算时以 BasePrice 为基准）。</summary>
    public double ClampToLimit(double price)
    {
        var basis = PreSettlement > 0 ? PreSettlement : BasePrice;
        if (basis <= 0) return price;
        var upper = RoundToTick(basis * (1 + LimitRate));
        var lower = RoundToTick(basis * (1 - LimitRate));
        return Math.Min(upper, Math.Max(lower, price));
    }

    /// <summary>价格对齐最小变动价位（随机游走不产生 tick 外的怪价）。</summary>
    public double RoundToTick(double price)
    {
        if (PriceTick <= 0) return price;
        return Math.Round(Math.Round(price / PriceTick) * PriceTick, 6);
    }
}
