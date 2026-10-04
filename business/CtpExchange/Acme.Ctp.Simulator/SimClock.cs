using System;
using System.Globalization;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>
/// 仿真柜台的时间来源（可替换 ⇒ 单测能构造任意时刻，不依赖 <see cref="DateTime.Now"/>）。
/// </summary>
/// <remarks>
/// 交易日推导遵循 CTP 的口径：夜盘（21:00 之后）属于**下一个**交易日。
/// 节假日表不参与（<see cref="CtpTradingCalendar"/> 亦不含），故「下一个自然工作日」即近似答案——
/// 仿真的目的是让交易日字段看起来合理，不是交易所结算。
/// </remarks>
public sealed class SimClock
{
    private readonly Func<DateTime> _now;

    /// <summary>用真实系统时钟。</summary>
    public SimClock() : this(() => DateTime.Now) { }

    /// <summary>用固定时钟（测试：<c>new SimClock(() => someLocalTime)</c>）。</summary>
    public SimClock(Func<DateTime> now) => _now = now;

    /// <summary>当前本地时间。</summary>
    public DateTime Now => _now();

    /// <summary>当前交易日（yyyyMMdd）。夜盘 21:00 后归下一个工作日。</summary>
    public string TradingDay => TradingDayOf(Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>HH:mm:ss（登录时间 / 报单时间 / 成交时间共用）。</summary>
    public string TimeOfDay => Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>HH:mm:ss.fff（tick 时间）。</summary>
    public string TickTime => Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>当前毫秒部分（<c>CtpTick.UpdateMillisec</c>）。</summary>
    public int Milliseconds => Now.Millisecond;

    /// <summary>日期 → 交易日（夜盘归次日工作日）。</summary>
    public static DateTime TradingDayOf(DateTime local)
    {
        var d = local.Kind == DateTimeKind.Utc ? local.ToLocalTime() : local;
        // 21:00 起属于下一交易日（夜盘）；02:30 前仍是前一日夜盘的延续，归属当日（已是「下一日」的日期）
        if (d.TimeOfDay >= TimeSpan.FromHours(21))
            return NextBusinessDay(d.Date);
        return d.Date;
    }

    /// <summary>下一个工作日（跳周六周日；不含节假日表）。</summary>
    private static DateTime NextBusinessDay(DateTime date)
    {
        var d = date.AddDays(1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }
}
