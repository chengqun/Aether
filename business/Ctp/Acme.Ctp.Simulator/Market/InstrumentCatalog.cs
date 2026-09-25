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
    /// <summary>未知合约的默认乘数（<see cref="SimOptions.AcceptUnknownInstruments"/> 为 true 时使用）。</summary>
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
    /// </summary>
    private static readonly Dictionary<string, SimInstrument> Varieties = new(StringComparer.OrdinalIgnoreCase)
    {
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
