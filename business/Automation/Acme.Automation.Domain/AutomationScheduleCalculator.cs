namespace Acme.Automation.Domain;

/// <summary>
/// 计算 <see cref="AutomationTask"/> 的下一次执行时间。供 Application（创建/更新）与
/// Infrastructure（执行后重算）共用，避免两处逻辑漂移。
/// </summary>
public static class AutomationScheduleCalculator
{
    // ExecuteTime 是用户输入的「中国时区墙钟」时刻（业务口径固定 +8，对齐活动热力图的
    // ChinaOffset=+8 约定，不依赖运行机器时区——出差/海外开发机也准；中国无 DST）。
    // from 是 UTC 时刻，故先把墙钟目标转成 UTC instant 再做比较/进位（旧实现把墙钟当
    // UTC，+8 时区下实际晚 8 小时触发）。
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>计算相对 <paramref name="from"/> 的下一次执行时间；禁用或无法计算返回 null。</summary>
    public static DateTime? ComputeNextRun(AutomationTask task, DateTime from)
    {
        if (!task.Enabled)
            return null;

        return task.ScheduleType switch
        {
            AutomationScheduleType.Daily => NextDaily(task.ExecuteTime, from),
            AutomationScheduleType.Interval => from.AddMinutes(task.IntervalMinutes <= 0 ? 1 : task.IntervalMinutes),
            AutomationScheduleType.Weekly => NextWeekly(task.ExecuteTime, task.Weekdays, from),
            _ => null
        };
    }

    private static DateTime NextDaily(TimeOnly executeTime, DateTime from)
    {
        // 今天墙钟时刻对应的 UTC instant = from.Date（UTC 零点）+ executeTime - 8h
        var todayUtc = from.Date.Add(executeTime.ToTimeSpan()).Add(-ChinaOffset);
        return todayUtc > from ? todayUtc : todayUtc.AddDays(1);
    }

    private static DateTime NextWeekly(TimeOnly executeTime, List<int> weekdays, DateTime from)
    {
        // 未选周几 → 回退到下周同一天
        if (weekdays is null || weekdays.Count == 0)
        {
            var todayUtc = from.Date.Add(executeTime.ToTimeSpan()).Add(-ChinaOffset);
            return todayUtc > from ? todayUtc : todayUtc.AddDays(7);
        }

        var set = new HashSet<int>(weekdays);
        // 从今天起扫描最多 7 天，找下一个选中周几的执行时刻
        for (var i = 0; i <= 7; i++)
        {
            var day = from.Date.AddDays(i);
            // ISO: 周一=1..周日=7
            var dow = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
            if (!set.Contains(dow)) continue;

            // 该 UTC 日的墙钟执行时刻：UTC 零点 -8h（= 该日中国零点附近）+ executeTime
            var at = day.Add(executeTime.ToTimeSpan()).Add(-ChinaOffset);
            if (at > from) return at;
        }

        // 兜底（理论不可达）
        return from.Date.AddDays(1).Add(executeTime.ToTimeSpan()).Add(-ChinaOffset);
    }
}
