namespace Acme.Automation.Domain;

/// <summary>
/// 计算 <see cref="AutomationTask"/> 的下一次执行时间。供 Application（创建/更新）与
/// Infrastructure（执行后重算）共用，避免两处逻辑漂移。
/// </summary>
public static class AutomationScheduleCalculator
{
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
        var todayAt = from.Date.Add(executeTime.ToTimeSpan());
        return todayAt > from ? todayAt : todayAt.AddDays(1);
    }

    private static DateTime NextWeekly(TimeOnly executeTime, List<int> weekdays, DateTime from)
    {
        // 未选周几 → 回退到下周同一天
        if (weekdays is null || weekdays.Count == 0)
        {
            var todayAt = from.Date.Add(executeTime.ToTimeSpan());
            return todayAt > from ? todayAt : todayAt.AddDays(7);
        }

        var set = new HashSet<int>(weekdays);
        // 从今天起扫描最多 7 天，找下一个选中周几的执行时刻
        for (var i = 0; i <= 7; i++)
        {
            var day = from.Date.AddDays(i);
            // ISO: 周一=1..周日=7
            var dow = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
            if (!set.Contains(dow)) continue;

            var at = day.Add(executeTime.ToTimeSpan());
            if (at > from) return at;
        }

        // 兜底（理论不可达）
        return from.Date.AddDays(1).Add(executeTime.ToTimeSpan());
    }
}
