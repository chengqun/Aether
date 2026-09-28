using System.Globalization;
using Acme.Ctp.Domain;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace Acme.Trading.Runtime.Lean;

/// <summary>
/// <see cref="CtpBrokerage"/> 的行情 partial：实现 <see cref="QuantConnect.Interfaces.IDataQueueHandler"/>。
/// Subscribe 经 <c>_aggregator.Add</c> 拿 enumerator + <c>_subscriptionManager.Subscribe</c> 记账（首次触发 CTP 行情订阅），
/// 并捕获每个订阅的 Lean Symbol + 分辨率（建 <see cref="BarBuilder"/>）。
/// CTP <c>TickReceived</c> → 按 instrumentId 查捕获的 Symbol → BarBuilder 聚合成 <see cref="CtpBar"/> → <c>_aggregator.Update</c> 推算法。
/// 回测 CtpBar 走 CSV，实盘 CtpBar 走 tick 聚合——同一数据类型，算法无感知（Lean backtest=live）。
/// </summary>
public partial class CtpBrokerage
{
    /// <summary>instrumentId → Lean Symbol（Subscribe 捕获，大小写不敏感——Lean 可能大写 ticker，CTP 用原始大小写）。</summary>
    private readonly Dictionary<string, Symbol> _symbolByInstrument = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Lean Symbol → BarBuilder（按订阅分辨率聚合 tick→bar）。</summary>
    private readonly Dictionary<Symbol, BarBuilder> _barBuilders = new();

    public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
    {
        EnsureInitialized();
        if (_aggregator == null)
        {
            Log.Error("CtpBrokerage.Subscribe: aggregator 未就绪");
            return null!;
        }
        var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
        _subscriptionManager!.Subscribe(dataConfig);
        // 捕获 Symbol + 建按分辨率的 bar 聚合器
        var sym = dataConfig.Symbol;
        _symbolByInstrument[sym.Value] = sym;
        _barBuilders[sym] = new BarBuilder(dataConfig.Increment);
        CtpLeanGateway.Log($"[Data] 订阅 {sym.Value} 增量={dataConfig.Increment}");
        return enumerator;
    }

    public void Unsubscribe(SubscriptionDataConfig dataConfig)
    {
        _subscriptionManager?.Unsubscribe(dataConfig);
        _aggregator?.Remove(dataConfig);
        _barBuilders.Remove(dataConfig.Symbol);
        CtpLeanGateway.Log($"[Data] 退订 {dataConfig.Symbol.Value}");
    }

    public void SetJob(LiveNodePacket job)
    {
        _job = job;
        EnsureInitialized();
        if (!IsConnected) Connect();
    }

    /// <summary>CTP Tick 回报 → 查订阅 Symbol → BarBuilder 聚合 → 完成的 CtpBar 推 aggregator。</summary>
    private void OnTickReceived(object? sender, CtpTick tick)
    {
        if (_aggregator == null) return;
        if (!_symbolByInstrument.TryGetValue(tick.InstrumentId, out var symbol)) return;   // 未订阅的合约过滤掉
        if (!_barBuilders.TryGetValue(symbol, out var builder)) return;
        try
        {
            var time = ParseTickTime(tick);
            var completed = builder.OnTick(time, (decimal)tick.LastPrice, symbol);
            if (completed != null) _aggregator.Update(completed);
        }
        catch (Exception ex)
        {
            CtpLeanGateway.Log($"[Data] tick 聚合失败 {tick.InstrumentId}: {ex.Message}");
        }
    }

    private int _tickSampleCount;
    private void LogTickSample(CtpTick tick)
    {
        var n = System.Threading.Interlocked.Increment(ref _tickSampleCount);
        if (n <= 5 || n % 200 == 0)
            CtpLeanGateway.Log($"[Tick] {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice}/{tick.BidVolume} ask={tick.AskPrice}/{tick.AskVolume} {tick.UpdateTime}.{tick.UpdateMillisec:D3} (#{n})");
    }

    // OnOrderReceived/OnTradeReceived（P2 报单/成交回报 → Lean OrderEvent 翻译）实现在 CtpBrokerage.Trading.cs

    private static DateTime ParseTickTime(CtpTick tick)
    {
        var dateStr = string.IsNullOrWhiteSpace(tick.ActionDay) ? DateTime.Now.ToString("yyyyMMdd") : tick.ActionDay;
        var timeStr = string.IsNullOrWhiteSpace(tick.UpdateTime) ? "00:00:00" : tick.UpdateTime;
        foreach (var fmt in new[] { "yyyyMMdd HH:mm:ss", "yyyy-MM-dd HH:mm:ss" })
        {
            if (DateTime.TryParseExact($"{dateStr} {timeStr}", fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return t.AddMilliseconds(tick.UpdateMillisec);
        }
        return DateTime.Now;
    }

    /// <summary>
    /// tick → CtpBar 聚合器：按订阅分辨率（config.Increment）把 tick 聚成 OHLCV bar。
    /// 回测 CSV 是预聚合 bar，实盘用本类把实时 tick 聚成同形态 bar——算法 slice.Get&lt;CtpBar&gt;() 两边一致。
    /// P1 不跟踪 volume（CTP Volume 是日累计，非 per-bar；SMA 策略只用 close 价）。
    /// <para><b>成棒时机（2026-09-28 修）</b>：tick/second 分辨率（&lt;1 分钟）每 tick 即一根单点 bar，<b>立即外发</b>；
    /// ≥分钟级仍是跨边界关棒（bar 是时间聚合，须等边界过掉）。原因：交易所模式的行情是<b>事件驱动</b>
    /// （挂单/撤单/成交才有 tick）——若等下一 tick 才产出上一根 bar，订阅快照（空闲市场的唯一一帧）永远不成棒
    /// ⇒ OnData 不触发 ⇒ 策略不下单 ⇒ 再无事件——市场自举死锁（实测：双 bot 启动后零交易）。</para>
    /// </summary>
    private sealed class BarBuilder
    {
        private readonly TimeSpan _increment;
        private DateTime _barTime;
        private decimal _open, _high, _low, _close;
        private bool _hasData;

        public BarBuilder(TimeSpan increment) { _increment = increment; }

        public CtpBar? OnTick(DateTime time, decimal price, Symbol symbol)
        {
            var boundary = Truncate(time, _increment);

            // tick/second：单点 bar 立即外发（见类注释——事件驱动行情的自举要求）
            if (_increment < TimeSpan.FromMinutes(1))
            {
                _barTime = boundary; _open = _high = _low = _close = price; _hasData = true;
                return new CtpBar
                {
                    Symbol = symbol, Time = _barTime, EndTime = _barTime.Add(_increment),
                    Open = _open, High = _high, Low = _low, Close = _close, Volume = 0, Value = _close,
                };
            }

            // ≥分钟级：跨边界时关上一根（原有语义）
            CtpBar? completed = null;
            if (_hasData && boundary != _barTime)
            {
                completed = new CtpBar
                {
                    Symbol = symbol, Time = _barTime, EndTime = _barTime.Add(_increment),
                    Open = _open, High = _high, Low = _low, Close = _close, Volume = 0, Value = _close,
                };
            }
            if (!_hasData || boundary != _barTime)
            {
                _barTime = boundary; _open = _high = _low = _close = price; _hasData = true;
            }
            else
            {
                if (price > _high) _high = price;
                if (price < _low) _low = price;
                _close = price;
            }
            return completed;
        }

        private static DateTime Truncate(DateTime t, TimeSpan inc)
        {
            if (inc >= TimeSpan.FromDays(1)) return t.Date;
            if (inc >= TimeSpan.FromHours(1)) return new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
            if (inc >= TimeSpan.FromMinutes(1)) return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            return t;   // tick/second：不截断，每 tick 一根
        }
    }
}
