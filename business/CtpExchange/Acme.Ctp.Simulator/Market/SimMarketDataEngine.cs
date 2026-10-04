using System;
using System.Collections.Generic;
using System.Globalization;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>仿真报价快照（买一/卖一/最新）。</summary>
public readonly record struct SimQuote(string InstrumentId, double BidPrice, double AskPrice, double LastPrice)
{
    /// <summary>对手价：买用卖一、卖用买一。</summary>
    public double OppositeOf(CtpDirection direction)
        => direction == CtpDirection.Buy ? AskPrice : BidPrice;
}

/// <summary>订单簿顶快照（行情源）。空侧价格为 0（真实 CTP 语义：单边市场/无对手）。</summary>
public readonly record struct BookTop(double BidPrice, int BidVolume, double AskPrice, int AskVolume);

/// <summary>
/// 行情引擎（<b>价格发现归簿</b>——2026-10-03 起 walk/回放已删除，交易所恒为订单簿模式）：
/// bid/ask = 订单簿顶（经 <see cref="BindBook"/> 注入的簿顶回调），last = 最新成交价（首笔前 = 昨结算基准），
/// tick <b>事件驱动</b>（<see cref="RecordTrade"/>/<see cref="OnBookChanged"/>）——挂单/撤单/成交即推。
/// <para>
/// <b>行情随柜台</b>：tick **恒外发**（<see cref="TickGenerated"/>）——host 按订阅连接路由推送；
/// sim 登录的客户端看到的行情价 = 撮合用的价（同一引擎），沙盒自洽。想要真实行情 ⇒ 客户端登录真实前置。
/// </para>
/// <para>
/// 会话统计（最新价/开高低/累计量）逐合约保存在 <see cref="MarketStats"/>——从首笔成交起算（真实交易所口径），
/// 跨重启经 <see cref="Restore"/> 从最后一条落盘成交 tick 续上。
/// </para>
/// </summary>
public sealed class SimMarketDataEngine
{
    private readonly SimClock _clock;
    private readonly Func<string, SimInstrument?> _instrumentOf;
    private readonly Dictionary<string, MarketStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>簿顶/持仓量提供者（核心持有簿，引擎持回调——<b>回调须在核心 <c>_gate</c> 内调用</b>）。</summary>
    private Func<string, BookTop?>? _bookTopOf;
    private Func<string, double>? _openInterestOf;

    /// <summary>新合成 tick（恒产生；核心 <c>TickProduced</c> 转发，host 按订阅连接路由推送）。</summary>
    public event EventHandler<CtpTick>? TickGenerated;

    public SimMarketDataEngine(SimClock clock, Func<string, SimInstrument?> instrumentOf)
    {
        _clock = clock;
        _instrumentOf = instrumentOf;
    }

    /// <summary>绑定订单簿顶/持仓量提供者（行情源；核心构造时接线）。</summary>
    public void BindBook(Func<string, BookTop?>? bookTopOf, Func<string, double>? openInterestOf)
    {
        _bookTopOf = bookTopOf;
        _openInterestOf = openInterestOf;
    }

    /// <summary>单合约的行情会话统计（最新价/开高低/累计量——首笔成交起算）。</summary>
    private sealed class MarketStats
    {
        public required SimInstrument Instrument { get; init; }
        public double Last { get; set; }
        public double Highest { get; set; }
        public double Lowest { get; set; }
        public double Open { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>
    /// 取某合约当前报价（首次访问时按合约元数据初始化统计）：bid/ask = 簿顶（空侧 0）、last = 最新成交价。
    /// </summary>
    public SimQuote? GetQuote(string instrumentId)
    {
        var s = EnsureStats(instrumentId);
        if (s is null) return null;
        var top = _bookTopOf?.Invoke(instrumentId);
        return new SimQuote(
            s.Instrument.InstrumentId,
            top?.BidPrice ?? 0,
            top?.AskPrice ?? 0,
            s.Instrument.RoundToTick(s.Last));
    }

    /// <summary>
    /// 成交回报驱动行情——最新价/最高/最低/开市价/累计量更新 + 立即外发 tick（含成交后簿顶）。
    /// <b>返回发出的这帧 tick</b>（调用方落盘——真实交易消息）。
    /// </summary>
    public CtpTick? RecordTrade(string instrumentId, double price, int volume)
    {
        var s = EnsureStats(instrumentId);
        if (s is null) return null;
        if (price > 0)
        {
            s.Last = price;
            s.Highest = Math.Max(s.Highest, price);
            s.Lowest = s.Lowest == 0 ? price : Math.Min(s.Lowest, price);
            if (s.Open == 0) s.Open = price;
        }
        if (volume > 0) s.Volume += volume;
        return EmitTick(s);
    }

    /// <summary>
    /// 簿变更（新挂单/撤单/成交离簿）后外发快照 tick（bid/ask = 新簿顶）。
    /// 未激活（无人订阅、没人碰过）的合约不推。
    /// </summary>
    public void OnBookChanged(string instrumentId)
    {
        if (!_stats.TryGetValue(instrumentId, out var s)) return;
        EmitTick(s);
    }

    /// <summary>重启恢复：用最后一条落盘成交 tick 续上会话统计（最新价/累计量/开高低）——不归零回昨结算基准。</summary>
    public void Restore(string instrumentId, CtpTick tick)
    {
        var s = EnsureStats(instrumentId);
        if (s is null) return;
        if (tick.LastPrice > 0) s.Last = tick.LastPrice;
        if (tick.Volume > 0) s.Volume = tick.Volume;
        if (tick.OpenPrice > 0) s.Open = tick.OpenPrice;
        if (tick.Highest > 0) s.Highest = tick.Highest;
        if (tick.Lowest > 0) s.Lowest = tick.Lowest;
    }

    /// <summary>tick：簿顶（含同价档聚合量）+ 成交流统计 + 持仓量。调用方语义上应持核心 <c>_gate</c>。</summary>
    private CtpTick EmitTick(MarketStats s)
    {
        var id = s.Instrument.InstrumentId;
        var top = _bookTopOf?.Invoke(id);
        return RaiseTick(s, top?.BidPrice ?? 0, top?.AskPrice ?? 0, top?.BidVolume ?? 0, top?.AskVolume ?? 0);
    }

    /// <summary>
    /// 激活合约的行情（创建统计，不改价格）。
    /// <para>
    /// <b>订阅即须激活</b>：<see cref="OnBookChanged"/> 只推**已激活**的合约，而统计何时诞生取决于
    /// 谁先碰过该合约——<see cref="GetQuote"/>/<see cref="RecordTrade"/> 会隐式创建（下单走这条路），
    /// 但「只订阅、不下单」的纯行情订阅不会。必须显式激活，否则订阅了也收不到快照与后续 tick。
    /// </para>
    /// </summary>
    /// <returns>合约元数据存在则 true（激活成功）。</returns>
    public bool Activate(string instrumentId) => EnsureStats(instrumentId) is not null;

    private CtpTick RaiseTick(MarketStats s, double bid, double ask, int bidVolume = 1, int askVolume = 1)
    {
        var instrument = s.Instrument;
        var tick = new CtpTick
        {
            InstrumentId = instrument.InstrumentId,
            ExchangeId = instrument.ExchangeId,
            TradingDay = _clock.TradingDay,
            ActionDay = _clock.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            UpdateTime = _clock.TickTime,
            UpdateMillisec = _clock.Milliseconds,
            LastPrice = instrument.RoundToTick(s.Last),
            Volume = s.Volume,
            BidPrice = bid,
            BidVolume = bidVolume,
            AskPrice = ask,
            AskVolume = askVolume,
            OpenInterest = _openInterestOf?.Invoke(instrument.InstrumentId) ?? 0,
            PreSettlement = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            PreClose = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice,
            OpenPrice = instrument.RoundToTick(s.Open),
            Highest = instrument.RoundToTick(s.Highest),
            Lowest = instrument.RoundToTick(s.Lowest),
        };
        TickGenerated?.Invoke(this, tick);
        return tick;
    }

    private MarketStats? EnsureStats(string instrumentId)
    {
        if (_stats.TryGetValue(instrumentId, out var s)) return s;
        var instrument = _instrumentOf(instrumentId);
        if (instrument is null) return null;
        var start = instrument.PreSettlement > 0 ? instrument.PreSettlement : instrument.BasePrice;
        if (start <= 0) start = 100;
        s = new MarketStats
        {
            Instrument = instrument,
            Last = start,
            Open = 0,      // 开市价/最高/最低从首笔成交起算（真实交易所口径）
            Highest = 0,
            Lowest = 0,
        };
        _stats[instrumentId] = s;
        return s;
    }
}
