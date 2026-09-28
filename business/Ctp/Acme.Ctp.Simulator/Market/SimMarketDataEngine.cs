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

/// <summary>订单簿顶快照（交易所模式的行情源）。空侧价格为 0（真实 CTP 语义：单边市场/无对手）。</summary>
public readonly record struct BookTop(double BidPrice, int BidVolume, double AskPrice, int AskVolume);

/// <summary>
/// 行情引擎（两种价格模型，由 <see cref="SimOptions.MarketMode"/> 决定）：
/// <list type="bullet">
/// <item><b>随机游走</b>（默认）：中枢价 ± 随机步，买一/卖一 = 中枢 ∓ 1 tick，定时器推进——庄家兜底用游走报价撮合。</item>
/// <item><b>交易所模式</b>：价格发现归簿——bid/ask = 订单簿顶（经 <see cref="BindBook"/> 注入的簿顶回调），
/// last = 最新成交价（首笔前 = 昨结算基准），tick 事件驱动（<see cref="RecordTrade"/>/<see cref="OnBookChanged"/>）。
/// 成交不驱动游走是 walk 模式的既有行为（pinned）；交易所模式下成交即行情。</item>
/// </list>
/// <para>
/// <b>行情随柜台</b>：tick **恒外发**（<see cref="TickGenerated"/>）——host 按订阅连接路由推送；
/// sim 登录的客户端看到的行情价 = 撮合引擎用的价（同一引擎），沙盒自洽。想要真实行情 ⇒ 客户端登录真实前置。
/// </para>
/// <para>
/// walk 价格模型：中枢价 ± 随机步（步长 = 最小变动价位 × [1, MaxWalkTicks]），夹在涨跌停板内。
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
    private readonly bool _exchange;

    /// <summary>交易所模式的簿顶/持仓量提供者（核心持有簿，引擎持回调——<b>回调须在核心 <c>_gate</c> 内调用</b>）。</summary>
    private Func<string, BookTop?>? _bookTopOf;
    private Func<string, double>? _openInterestOf;

    /// <summary>新合成 tick（恒产生；核心 <c>TickProduced</c> 转发，host 按订阅连接路由推送）。</summary>
    public event EventHandler<CtpTick>? TickGenerated;

    public SimMarketDataEngine(SimOptions options, SimClock clock, Func<string, SimInstrument?> instrumentOf)
    {
        _options = options;
        _clock = clock;
        _instrumentOf = instrumentOf;
        _exchange = options.MarketMode == SimMarketMode.Exchange;
    }

    /// <summary>绑定订单簿顶/持仓量提供者（交易所模式行情源；核心构造时接线）。</summary>
    public void BindBook(Func<string, BookTop?>? bookTopOf, Func<string, double>? openInterestOf)
    {
        _bookTopOf = bookTopOf;
        _openInterestOf = openInterestOf;
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

    /// <summary>
    /// 取某合约当前报价（首次访问时按合约元数据初始化）。交易所模式：bid/ask = 簿顶（空侧 0）、last = 最新成交价。
    /// </summary>
    public SimQuote? GetQuote(string instrumentId)
    {
        var w = EnsureWalk(instrumentId);
        if (w is null) return null;
        if (_exchange)
        {
            var top = _bookTopOf?.Invoke(instrumentId);
            return new SimQuote(
                w.Instrument.InstrumentId,
                top?.BidPrice ?? 0,
                top?.AskPrice ?? 0,
                w.Instrument.RoundToTick(w.Last));
        }
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
    /// 交易所模式：成交回报驱动行情——最新价/最高/最低/开市价/累计量更新 + 立即外发 tick（含成交后簿顶）。
    /// <b>返回发出的这帧 tick</b>（调用方落盘——真实交易消息；walk 模式无操作返回 null）。
    /// </summary>
    public CtpTick? RecordTrade(string instrumentId, double price, int volume)
    {
        if (!_exchange) return null;
        var w = EnsureWalk(instrumentId);
        if (w is null) return null;
        if (price > 0)
        {
            w.Last = price;
            w.Highest = Math.Max(w.Highest, price);
            w.Lowest = w.Lowest == 0 ? price : Math.Min(w.Lowest, price);
            if (w.Open == 0) w.Open = price;
        }
        if (volume > 0) w.Volume += volume;
        return EmitExchangeTick(w);
    }

    /// <summary>
    /// 交易所模式：簿变更（新挂单/撤单/成交离簿）后外发快照 tick（bid/ask = 新簿顶）。
    /// 未激活（无人订阅、没人碰过）的合约不推。walk 模式无操作。
    /// </summary>
    public void OnBookChanged(string instrumentId)
    {
        if (!_exchange) return;
        if (!_walks.TryGetValue(instrumentId, out var w)) return;
        EmitExchangeTick(w);
    }

    /// <summary>重启恢复：用最后一条落盘成交 tick 续上会话统计（最新价/累计量/开高低）——不归零回昨结算基准。</summary>
    public void Restore(string instrumentId, CtpTick tick)
    {
        var w = EnsureWalk(instrumentId);
        if (w is null) return;
        if (tick.LastPrice > 0) { w.Last = tick.LastPrice; w.Mid = tick.LastPrice; }
        if (tick.Volume > 0) w.Volume = tick.Volume;
        if (tick.OpenPrice > 0) w.Open = tick.OpenPrice;
        if (tick.Highest > 0) w.Highest = tick.Highest;
        if (tick.Lowest > 0) w.Lowest = tick.Lowest;
    }

    /// <summary>交易所模式的 tick：簿顶（含同价档聚合量）+ 成交流统计 + 持仓量。调用方语义上应持核心 <c>_gate</c>。</summary>
    private CtpTick? EmitExchangeTick(Walk w)
    {
        var id = w.Instrument.InstrumentId;
        var top = _bookTopOf?.Invoke(id);
        return RaiseTick(w, top?.BidPrice ?? 0, top?.AskPrice ?? 0, top?.BidVolume ?? 0, top?.AskVolume ?? 0);
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

    /// <summary>推进一个 tick（全部已激活合约）。返回推进后的报价表。交易所模式无游走——恒空表。</summary>
    public IReadOnlyList<SimQuote> Advance()
    {
        if (_exchange) return Array.Empty<SimQuote>();
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

    private CtpTick RaiseTick(Walk w, double bid, double ask, int bidVolume = 1, int askVolume = 1)
    {
        var instrument = w.Instrument;
        var tick = new CtpTick
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
            BidVolume = bidVolume,
            AskPrice = ask,
            AskVolume = askVolume,
            OpenInterest = _exchange ? _openInterestOf?.Invoke(instrument.InstrumentId) ?? 0 : 0,
            PreSettlement = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            PreClose = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            OpenPrice = instrument.RoundToTick(w.Open),
            Highest = instrument.RoundToTick(w.Highest),
            Lowest = instrument.RoundToTick(w.Lowest),
        };
        TickGenerated?.Invoke(this, tick);
        return tick;
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
            Open = _exchange ? 0 : start,      // 交易所模式：开市价/最高/最低从首笔成交起算（真实口径）
            Highest = _exchange ? 0 : start,
            Lowest = _exchange ? 0 : start,
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
