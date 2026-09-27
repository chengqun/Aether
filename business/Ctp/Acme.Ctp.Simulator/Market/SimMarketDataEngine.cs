using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>仿真报价快照（买一/卖一/最新）。</summary>
public readonly record struct SimQuote(string InstrumentId, double BidPrice, double AskPrice, double LastPrice)
{
    /// <summary>对手价：买用卖一、卖用买一。</summary>
    public double OppositeOf(CtpDirection direction)
        => direction == CtpDirection.Buy ? AskPrice : BidPrice;
}

/// <summary>
/// 随机游走行情引擎。
/// <para>
/// <b>行情随柜台</b>：合成 tick **恒外发**（<see cref="TickGenerated"/>）——host 按订阅连接路由推送；
/// sim 登录的客户端看到的行情价 = 撮合引擎用的价（同一引擎），沙盒自洽。想要真实行情 ⇒ 客户端登录真实前置。
/// </para>
/// <para>
/// 价格模型：中枢价 ± 随机步（步长 = 最小变动价位 × [1, MaxWalkTicks]），夹在涨跌停板内；
/// 买一/卖一 = 中枢 ∓ 1 tick。**无对手方流量**——故撮合以「中枢价」为可成交基准（见 <see cref="SimpleMatchingEngine"/>）。
/// </para>
/// <para>
/// 可复现：每个合约一个独立 <see cref="Random"/>，种子 = <see cref="SimOptions.Seed"/> ^ 合约名稳定哈希
/// （**不用 <c>string.GetHashCode</c>**——它每进程随机化，会破坏可复现性）。
/// </para>
/// </summary>
public sealed class SimMarketDataEngine
{
    private readonly SimOptions _options;
    private readonly SimClock _clock;
    private readonly Func<string, SimInstrument?> _instrumentOf;
    private readonly Dictionary<string, Walk> _walks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>新合成 tick（恒产生；核心 <c>TickProduced</c> 转发，host 按订阅连接路由推送）。</summary>
    public event EventHandler<CtpTick>? TickGenerated;

    public SimMarketDataEngine(SimOptions options, SimClock clock, Func<string, SimInstrument?> instrumentOf)
    {
        _options = options;
        _clock = clock;
        _instrumentOf = instrumentOf;
    }

    private sealed class Walk
    {
        public required SimInstrument Instrument { get; init; }
        public required Random Rng { get; init; }
        public double Mid { get; set; }
        public double Last { get; set; }
        public double Highest { get; set; }
        public double Lowest { get; set; }
        public double Open { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>取某合约当前报价（首次访问时按合约元数据初始化中枢价）。</summary>
    public SimQuote? GetQuote(string instrumentId)
    {
        var w = EnsureWalk(instrumentId);
        if (w is null) return null;
        var tick = w.Instrument.PriceTick > 0 ? w.Instrument.PriceTick : 1;
        return new SimQuote(
            w.Instrument.InstrumentId,
            w.Instrument.RoundToTick(w.Mid - tick),
            w.Instrument.RoundToTick(w.Mid + tick),
            w.Instrument.RoundToTick(w.Last));
    }

    /// <summary>
    /// 手动注入报价（测试 + **历史 tick 回放**驱动；同时更新内部中枢，使后续游走从该点继续）。
    /// <paramref name="volume"/>（可选累计成交量）非空时直接置位——回放保真用。
    /// </summary>
    public void Inject(string instrumentId, double bid, double ask, double last, long? volume = null)
    {
        var w = EnsureWalk(instrumentId);
        if (w is null) return;
        var mid = (bid + ask) / 2;
        w.Mid = mid > 0 ? mid : last;
        w.Last = last > 0 ? last : w.Mid;
        w.Highest = Math.Max(w.Highest, w.Last);
        w.Lowest = w.Lowest == 0 ? w.Last : Math.Min(w.Lowest, w.Last);
        if (w.Open == 0) w.Open = w.Last;
        if (volume is { } v) w.Volume = v;
        RaiseTick(w, bid, ask);
    }

    /// <summary>
    /// 激活合约的行情游走（创建 <c>walk</c>，不改价格）。
    /// <para>
    /// <b>订阅即须激活</b>：<see cref="Advance"/> 只推进**已存在**的 walk，而 walk 何时诞生取决于
    /// 谁先碰过该合约——<see cref="GetQuote"/>/<see cref="Inject"/> 会隐式创建（下单/注入行情走这条路），
    /// 但「只订阅、不下单」的纯行情订阅不会。仿真行情模式下必须显式激活，
    /// 否则订阅了也永远收不到 tick（早期缺陷：期货页订阅后行情恒空）。
    /// </para>
    /// </summary>
    /// <returns>合约元数据存在则 true（激活成功）。</returns>
    public bool Activate(string instrumentId) => EnsureWalk(instrumentId) is not null;

    /// <summary>推进一个 tick（全部已激活合约）。返回推进后的报价表。</summary>
    public IReadOnlyList<SimQuote> Advance()
    {
        var result = new List<SimQuote>();
        // 排序保证推进顺序确定（单测可复现）
        foreach (var key in _walks.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var w = _walks[key];
            Step(w);
            var tick = w.Instrument.PriceTick > 0 ? w.Instrument.PriceTick : 1;
            var q = new SimQuote(
                w.Instrument.InstrumentId,
                w.Instrument.RoundToTick(w.Mid - tick),
                w.Instrument.RoundToTick(w.Mid + tick),
                w.Instrument.RoundToTick(w.Last));
            result.Add(q);
            RaiseTick(w, q.BidPrice, q.AskPrice);
        }
        return result;
    }

    private void Step(Walk w)
    {
        if (_options.GateByTradingTime && !CtpTradingCalendar.IsTradingTime(_clock.Now))
            return;

        var tick = w.Instrument.PriceTick > 0 ? w.Instrument.PriceTick : 1;
        var steps = w.Rng.Next(-_options.MaxWalkTicks, _options.MaxWalkTicks + 1);
        if (steps == 0) steps = w.Rng.Next(0, 2) == 0 ? -1 : 1;

        var mid = w.Instrument.ClampToLimit(w.Mid + steps * tick);
        w.Mid = mid;
        w.Last = mid;
        w.Highest = Math.Max(w.Highest, mid);
        w.Lowest = w.Lowest == 0 ? mid : Math.Min(w.Lowest, mid);
        if (w.Open == 0) w.Open = mid;
        w.Volume += w.Rng.Next(1, 50);
    }

    private void RaiseTick(Walk w, double bid, double ask)
    {
        var instrument = w.Instrument;
        TickGenerated?.Invoke(this, new CtpTick
        {
            InstrumentId = instrument.InstrumentId,
            ExchangeId = instrument.ExchangeId,
            TradingDay = _clock.TradingDay,
            ActionDay = _clock.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            UpdateTime = _clock.TickTime,
            UpdateMillisec = _clock.Milliseconds,
            LastPrice = instrument.RoundToTick(w.Last),
            Volume = w.Volume,
            BidPrice = bid,
            BidVolume = 1,
            AskPrice = ask,
            AskVolume = 1,
            PreSettlement = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            PreClose = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            OpenPrice = instrument.RoundToTick(w.Open),
            Highest = instrument.RoundToTick(w.Highest),
            Lowest = instrument.RoundToTick(w.Lowest),
        });
    }

    private Walk? EnsureWalk(string instrumentId)
    {
        if (_walks.TryGetValue(instrumentId, out var w)) return w;
        var instrument = _instrumentOf(instrumentId);
        if (instrument is null) return null;
        var start = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice;
        if (start <= 0) start = 100;
        w = new Walk
        {
            Instrument = instrument,
            Rng = new Random(unchecked(_options.Seed ^ StableHash(instrumentId))),
            Mid = start,
            Last = start,
            Open = start,
            Highest = start,
            Lowest = start,
        };
        _walks[instrumentId] = w;
        return w;
    }

    /// <summary>跨进程稳定的字符串哈希（FNV-1a 32 位）——保证同种子可复现。</summary>
    internal static int StableHash(string s)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            return (int)hash;
        }
    }
}
