using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>一次撮合尝试的结果。<paramref name="FillVolume"/> 为 0 表示未成交。</summary>
public readonly record struct MatchOutcome(int FillVolume, double FillPrice)
{
    public bool Filled => FillVolume > 0;
    public static readonly MatchOutcome None = new(0, 0);
}

/// <summary>
/// 撮合引擎。仿真的 Phase 1 实现刻意极简：**没有对手方订单簿**——
/// 价格来自随机游走（或真实行情），流动性用一个「单次最大成交手数」上限模拟。
/// </summary>
public interface IMatchingEngine
{
    /// <summary>
    /// 尝试撮合一笔活单。无副作用（不改订单、不记账），由调用方按结果推进状态。
    /// </summary>
    /// <param name="order">活单（未成交部分 = <c>VolumeTotalOriginal - VolumeTraded</c>）。</param>
    /// <param name="quote">该合约当前报价；null ⇒ 无行情，不成交。</param>
    /// <param name="instrument">合约元数据。</param>
    MatchOutcome Match(CtpOrder order, SimQuote? quote, SimInstrument instrument);
}

/// <summary>
/// 简单撮合：市价单按对手价立即成交；限价单在「买价 ≥ 卖一 / 卖价 ≤ 买一」时成交。
/// 成交量受 <see cref="SimOptions.MaxFillVolume"/> 限制（模拟流动性），
/// 并可经 <see cref="SimOptions.PartialFillChance"/> 概率性再切一笔更小的量（构造部分成交场景）。
/// </summary>
/// <remarks>
/// 成交价的取法：**按对手价成交**（买单吃卖一、卖单吃买一），这是最贴近真实的简化——
/// 不赚「限价与市场价的差」的便宜，但也不会出现「以优于对手的价格成交」这种不可能事件。
/// </remarks>
public sealed class SimpleMatchingEngine : IMatchingEngine
{
    private readonly SimOptions _options;
    private readonly Random _rng;

    public SimpleMatchingEngine(SimOptions options)
    {
        _options = options;
        // 与行情引擎分开的随机源：成交量随机不应被行情推进的顺序影响
        _rng = new Random(unchecked(options.Seed ^ 0x5EED));
    }

    /// <summary>可注入的随机源（测试用；null ⇒ 用内部种子随机器）。</summary>
    public SimpleMatchingEngine(SimOptions options, Random rng)
    {
        _options = options;
        _rng = rng;
    }

    public MatchOutcome Match(CtpOrder order, SimQuote? quote, SimInstrument instrument)
    {
        if (quote is null) return MatchOutcome.None;

        var remaining = order.VolumeTotalOriginal - order.VolumeTraded;
        if (remaining <= 0) return MatchOutcome.None;

        var q = quote.Value;
        double fillPrice;
        if (order.PriceType == CtpOrderPriceType.Market)
        {
            fillPrice = q.OppositeOf(order.Direction);
        }
        else
        {
            // 限价：买单要求卖一 ≤ 限价；卖单要求买一 ≥ 限价
            var crossed = order.Direction == CtpDirection.Buy
                ? q.AskPrice <= order.LimitPrice + 1e-9
                : q.BidPrice >= order.LimitPrice - 1e-9;
            if (!crossed) return MatchOutcome.None;
            fillPrice = q.OppositeOf(order.Direction);
        }

        if (fillPrice <= 0) return MatchOutcome.None;

        var volume = remaining;
        if (_options.MaxFillVolume > 0 && volume > _options.MaxFillVolume)
            volume = _options.MaxFillVolume;

        // 概率性「只吃到一部分」——用于构造部分成交场景（默认 0 ⇒ 不启用）
        if (volume > 1 && _options.PartialFillChance > 0 && _rng.NextDouble() < _options.PartialFillChance)
            volume = Math.Max(1, _rng.Next(1, volume));

        return new MatchOutcome(volume, instrument.RoundToTick(fillPrice));
    }
}
