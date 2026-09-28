using System.Diagnostics;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 做市商义务监测报告（每合约一行；<see cref="SimTradingCore.QueryMakerObligations"/> 返回）。
/// 占比/豁免口径：双边连续报价时间占比 = 双边报价时长 / 应报价时长；触限单边报价期<b>豁免</b>
/// （不计入分母——真实做市协议的持仓豁免条款）。违约 = 价差超限 / 报价量不足 / 持仓超限的<b>进入</b>次数。
/// </summary>
public sealed record MmObligationReport(
    string InstrumentId,
    double QuoteTimeRatio,
    bool TwoSidedNow,
    bool MeetsRatio,
    int Violations,
    int LongLots,
    int ShortLots);

/// <summary>
/// 做市商义务监测器（交易所约束层的统计面）：由核心按 <see cref="SimOptions.MmMonitorIntervalMs"/>
/// 采样驱动（在 <c>_gate</c> 内），无锁自守（单调用方）。仅统计不强制——强制在
/// <c>RunMarketMaker</c> 的报价钳制/仓位闸门里。
/// </summary>
internal sealed class MmObligationMonitor
{
    private sealed class Stat
    {
        public long TotalMs;
        public long QuotedMs;
        public int Violations;
        public bool WasViolating;
        public int LongLots;
        public int ShortLots;
        public bool TwoSided;
    }

    private readonly Dictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(string instrumentId, bool twoSided, bool spreadOk, bool volumeOk, bool positionOk,
        bool exempt, int longLots, int shortLots, int elapsedMs)
    {
        var s = GetOrAdd(instrumentId);
        s.TwoSided = twoSided;
        s.LongLots = longLots;
        s.ShortLots = shortLots;
        if (!exempt)
        {
            s.TotalMs += elapsedMs;
            if (twoSided) s.QuotedMs += elapsedMs;
        }

        var violating = (twoSided && (!spreadOk || !volumeOk)) || !positionOk;
        if (violating && !s.WasViolating)
        {
            s.Violations++;
            Trace.WriteLine(
                $"[CTP-SIM] 做市商义务违约（{instrumentId}）：{(twoSided && !spreadOk ? "价差超限 " : "")}" +
                $"{(twoSided && !volumeOk ? "报价量不足 " : "")}{(!positionOk ? "持仓超限" : "")}");
        }
        s.WasViolating = violating;
    }

    public IReadOnlyList<MmObligationReport> Report(double minRatio)
        => _stats.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MmObligationReport(
                kv.Key,
                kv.Value.TotalMs > 0 ? (double)kv.Value.QuotedMs / kv.Value.TotalMs : 0,
                kv.Value.TwoSided,
                kv.Value.TotalMs > 0 && (double)kv.Value.QuotedMs / kv.Value.TotalMs >= minRatio,
                kv.Value.Violations,
                kv.Value.LongLots,
                kv.Value.ShortLots))
            .ToList();

    private Stat GetOrAdd(string id)
    {
        if (!_stats.TryGetValue(id, out var s))
        {
            s = new Stat();
            _stats[id] = s;
        }
        return s;
    }
}
