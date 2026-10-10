using System;
using System.Collections.Generic;
using System.Linq;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 内置合约表种子 + 「未知合约」的兜底合成。
/// <para>
/// 种子只覆盖仓内已在用的品种（<c>ag2610</c> 等，见 <c>CtpBrokerageTradingTests.FakeCtpSession</c> 与期货页默认值），
/// 乘数/保证金率取近似真实值——仿真的目的不是记账精确到分，而是让保证金冻结、涨跌停、tick 对齐这些**机制**能跑起来。
/// </para>
/// </summary>
public static class InstrumentCatalog
{
    /// <summary>未知合约的默认乘数（上市表自定义 id 经合成兜底时使用）。</summary>
    public const double DefaultUnknownVolumeMultiple = 10;

    /// <summary>未知合约的默认保证金率。</summary>
    public const double DefaultUnknownMarginRate = 0.10;

    /// <summary>未知合约的默认最小变动价位。</summary>
    public const double DefaultUnknownPriceTick = 1;

    /// <summary>主力月份后缀（2610/2612/2701…）从代码中剥掉得到品种前缀，用于查品种元数据。</summary>
    public static string VarietyOf(string instrumentId)
    {
        var i = 0;
        while (i < instrumentId.Length && !char.IsDigit(instrumentId[i])) i++;
        return i == 0 ? instrumentId : instrumentId[..i];
    }

    /// <summary>
    /// 品种级元数据（不含具体月份）——月份只影响代码，不影响乘数/保证金率/最小变动价位，
    /// 故未知月份（如用户的真实持仓 <c>ag2602</c>）能按品种自动派生。
    /// <para>
    /// <c>zx</c> 品种 = 本交易所自有品种（「我就是交易所」）：zxNNN 系列（zx001/zx002…）按此派生——
    /// 上市表默认 <c>zx001</c>（2 字母+3 数字——对齐官方合约命名，sim001@3字母致前置解析器拒识，2026-10-01 实证）。
    /// 价格发现归订单簿（挂单/成交驱动）；真实行情转发明确不做（本柜台是自己的交易所）。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, SimInstrument> Varieties = new(StringComparer.OrdinalIgnoreCase)
    {
        // zx = BTC 对标品种（2026-10-05 起）：maker/taker 差异化手续费（挂 2bp / 吃 5bp，永续 VIP0 量级）。
        // 其余真实品种不设 ⇒ 回退单一 CommissionRate（CTP 双边同费形态）。
        ["zx"] = new SimInstrument { InstrumentId = "zx", ExchangeId = "SIM", VolumeMultiple = 10, MarginRate = 0.10, PriceTick = 1, BasePrice = 3800, PreSettlement = 3798, MakerCommissionRate = 0.0002, TakerCommissionRate = 0.0005 },
        // ax = Aether 现货币（批 4，2026-10-06；2026-10-10 开空解禁）：CTP 现货映射——买=开多/卖=开空，
        // 双向持仓与期货一致（开空解禁=用户拍板「现货也可以做空，一个品种就够」——保证金全程锁定保资金安全，
        // 场内 OI 可超链上真币（纸面头寸=期货常态）；凭空多头的提币受链上 custody 约束（链拒后对账自愈回滚，
        // 零损失）、空头不能提币（Burn 只烧 Long）——「场内随便开、链上真币一个不会多」双向兜底）。
        // 1 手 = 1 币；MarginRate=1.0（全额占用——开多=「付款」、开空=全额保证金）。充币铸造
        // （CreditSpotPosition）仍是链上资产入场的唯一闸门（桥对账只认 Long 持仓）。
        // maker/taker 费率镜像 zx（BTC 对标）。PriceTick=0.01（精细报价——币价应该能分到分级精度）。
        ["ax"] = new SimInstrument { InstrumentId = "ax", ExchangeId = "AETHER", VolumeMultiple = 1, MarginRate = 1.0, PriceTick = 0.01, BasePrice = 100, PreSettlement = 100, IsSpot = true, MakerCommissionRate = 0.0002, TakerCommissionRate = 0.0005 },
        ["ag"] = new SimInstrument { InstrumentId = "ag", ExchangeId = "SHFE", VolumeMultiple = 15, MarginRate = 0.12, PriceTick = 1, BasePrice = 7000, PreSettlement = 6990 },
        ["au"] = new SimInstrument { InstrumentId = "au", ExchangeId = "SHFE", VolumeMultiple = 1000, MarginRate = 0.10, PriceTick = 0.02, BasePrice = 560, PreSettlement = 558 },
        ["cu"] = new SimInstrument { InstrumentId = "cu", ExchangeId = "SHFE", VolumeMultiple = 5, MarginRate = 0.12, PriceTick = 10, BasePrice = 78000, PreSettlement = 77900 },
        ["rb"] = new SimInstrument { InstrumentId = "rb", ExchangeId = "SHFE", VolumeMultiple = 10, MarginRate = 0.10, PriceTick = 1, BasePrice = 3200, PreSettlement = 3198 },
        ["m"] = new SimInstrument { InstrumentId = "m", ExchangeId = "DCE", VolumeMultiple = 10, MarginRate = 0.08, PriceTick = 1, BasePrice = 3000, PreSettlement = 2998 },
        ["i"] = new SimInstrument { InstrumentId = "i", ExchangeId = "DCE", VolumeMultiple = 100, MarginRate = 0.13, PriceTick = 0.5, BasePrice = 780, PreSettlement = 778 },
        ["ta"] = new SimInstrument { InstrumentId = "ta", ExchangeId = "CZCE", VolumeMultiple = 5, MarginRate = 0.09, PriceTick = 2, BasePrice = 5200, PreSettlement = 5190 },
        ["IF"] = new SimInstrument { InstrumentId = "IF", ExchangeId = "CFFEX", VolumeMultiple = 300, MarginRate = 0.12, PriceTick = 0.2, BasePrice = 3900, PreSettlement = 3890 },
        ["sc"] = new SimInstrument { InstrumentId = "sc", ExchangeId = "INE", VolumeMultiple = 1000, MarginRate = 0.13, PriceTick = 0.1, BasePrice = 560, PreSettlement = 558 },
    };

    /// <summary>带月份的种子合约（默认装载集合）——月份取仓内已在用的示例。</summary>
    public static IReadOnlyList<SimInstrument> Seed() => new[]
    {
        With("ag", "ag2610"),
        With("au", "au2612"),
        With("cu", "cu2610"),
        With("rb", "rb2610"),
        With("m", "m2701"),
        With("i", "i2701"),
        With("ta", "ta2610"),
        With("IF", "IF2610"),
        With("sc", "sc2610"),
    };

    private static SimInstrument With(string variety, string instrumentId)
    {
        var v = Varieties[variety];
        return v with { InstrumentId = instrumentId };
    }

    /// <summary>
    /// 查不到精确合约时按品种派生一个（<c>ag2602</c> → 用 <c>ag</c> 的元数据）。
    /// 都不认识 → 返回 null，由调用方决定拒绝还是兜底合成。
    /// </summary>
    public static SimInstrument? DeriveByVariety(string instrumentId, IReadOnlyDictionary<string, SimInstrument> known)
    {
        if (known.TryGetValue(instrumentId, out var exact)) return exact;
        var variety = VarietyOf(instrumentId);
        return Varieties.TryGetValue(variety, out var v) ? v with { InstrumentId = instrumentId } : null;
    }

    /// <summary>完全不认识的合约：用下单价合成一个能撮合的最小合约（严格模式下不走这里）。</summary>
    public static SimInstrument Synthesize(string instrumentId, double referencePrice)
    {
        var price = referencePrice > 0 ? referencePrice : 100;
        return new SimInstrument
        {
            InstrumentId = instrumentId,
            // 交易所未知时留空——CTP 的撤单定位键用 ExchangeId 仅为回显，空串不影响撤单（按 FrontId/SessionId/OrderRef 定位）
            ExchangeId = "",
            VolumeMultiple = DefaultUnknownVolumeMultiple,
            MarginRate = DefaultUnknownMarginRate,
            PriceTick = DefaultUnknownPriceTick,
            BasePrice = price,
            PreSettlement = price,
        };
    }
}
