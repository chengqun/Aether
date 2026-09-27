using System.Diagnostics;

namespace Acme.Ctp.Simulator;

/// <summary>一条回放 tick 行（CSV 解析产物）。</summary>
/// <param name="At">行内时间（只用于行间差 pacing，不与墙钟对齐）。</param>
/// <param name="InstrumentId">合约号。</param>
/// <param name="Bid">买一。</param>
/// <param name="Ask">卖一。</param>
/// <param name="Last">最新价。</param>
/// <param name="Volume">累计成交量（可选——CSV 无该列则 null，走引擎累计）。</param>
internal sealed record ReplayTick(TimeSpan At, string InstrumentId, double Bid, double Ask, double Last, long? Volume);

/// <summary>
/// 历史 tick 回放驱动：「我就是交易所」的价格源之一——按 CSV 时间轴驱动
/// <see cref="SimTradingCore.InjectQuote"/>（注价即重试挂单，撮合/推送/行情板全复用）。
/// <para>
/// CSV 格式（**首行必须是表头**）：<c>time,instrument,bid,ask,last[,volume]</c>——
/// time 取 <c>HH:mm:ss[.fff]</c>（跨午夜回绕按次日处理；只用于**行间差**，不与墙钟对齐）；
/// volume 可选（累计量）。速度倍率 &gt;1 快放、&lt;1 慢放；loop 到文件尾停 1 秒后从头再来（沙盒常有市）。
/// </para>
/// <para>回放期间调用方应停掉随机 feed（<see cref="SimTradingCore.StopMarketFeed"/>）——否则游走会在回放价上再加噪。</para>
/// </summary>
internal sealed class TickReplayDriver : IDisposable
{
    private readonly IReadOnlyList<ReplayTick> _ticks;
    private readonly Action<ReplayTick> _inject;
    private readonly double _speed;
    private readonly bool _loop;
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;

    private TickReplayDriver(IReadOnlyList<ReplayTick> ticks, Action<ReplayTick> inject, double speed, bool loop)
    {
        _ticks = ticks;
        _inject = inject;
        _speed = speed;
        _loop = loop;
    }

    /// <summary>
    /// 解析并启动回放。文件不存在 / 表头不符 / 无有效行 ⇒ <see cref="ArgumentException"/>
    /// （调用方 host 侧容错：记日志回落随机 feed）。坏行跳过（Trace）。
    /// </summary>
    public static TickReplayDriver Start(string csvPath, Action<ReplayTick> inject, double speed = 1, bool loop = true)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) throw new ArgumentException("回放文件路径为空。", nameof(csvPath));
        if (!File.Exists(csvPath)) throw new ArgumentException($"回放文件不存在：{csvPath}", nameof(csvPath));

        var ticks = Parse(csvPath);
        if (ticks.Count == 0) throw new ArgumentException($"回放文件无有效行：{csvPath}", nameof(csvPath));

        var driver = new TickReplayDriver(ticks, inject, speed > 0 ? speed : 1, loop);
        driver._run = Task.Run(driver.RunAsync);
        return driver;
    }

    /// <summary>解析 CSV（表头必须含 time/instrument/bid/ask/last；volume 可选）。</summary>
    private static IReadOnlyList<ReplayTick> Parse(string path)
    {
        var ticks = new List<ReplayTick>();
        var headerSeen = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!headerSeen)
            {
                if (!line.Contains("time", StringComparison.OrdinalIgnoreCase) || !line.Contains("bid", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"回放 CSV 首行必须是表头 time,instrument,bid,ask,last[,volume]，实际：「{line}」");
                headerSeen = true;
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 5
                || !TimeSpan.TryParse(parts[0].Trim(), out var at)
                || !double.TryParse(parts[2].Trim(), out var bid)
                || !double.TryParse(parts[3].Trim(), out var ask)
                || !double.TryParse(parts[4].Trim(), out var last))
            {
                Trace.WriteLine($"[CTP-SIM] 回放行解析失败（跳过）：{line}");
                continue;
            }
            long? volume = parts.Length >= 6 && long.TryParse(parts[5].Trim(), out var v) ? v : null;
            ticks.Add(new ReplayTick(at, parts[1].Trim(), bid, ask, last, volume));
        }
        return ticks;
    }

    private async Task RunAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < _ticks.Count; i++)
            {
                if (token.IsCancellationRequested) return;
                if (i > 0)
                {
                    var delta = _ticks[i].At - _ticks[i - 1].At;
                    if (delta < TimeSpan.Zero) delta += TimeSpan.FromDays(1);   // 跨午夜回绕（夜盘→日盘）
                    var wait = delta.TotalMilliseconds / _speed;
                    if (wait > 0)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false); }
                        catch (TaskCanceledException) { return; }
                    }
                }
                _inject(_ticks[i]);
            }

            if (!_loop) return;
            try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }   // 文件尾停 1s 再循环
            catch (TaskCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 取消/聚合异常都忽略——驱动是尽力而为 */ }
        _cts.Dispose();
    }
}
