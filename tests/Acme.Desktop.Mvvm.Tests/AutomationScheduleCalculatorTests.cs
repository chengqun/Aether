using Acme.Automation.Domain;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// AutomationScheduleCalculator 时区语义回归测试——锁「ExecuteTime 当 UTC 用，+8 时区实际晚 8 小时触发」的修复。
/// ExecuteTime = 用户输入的「中国时区墙钟」，ComputeNextRun 的 from 是 UTC。
/// </summary>
public class AutomationScheduleCalculatorTests
{
    private static AutomationTask Daily(TimeOnly at) => new()
    {
        Enabled = true,
        ScheduleType = AutomationScheduleType.Daily,
        ExecuteTime = at,
    };

    private static AutomationTask Weekly(TimeOnly at, params int[] weekdays) => new()
    {
        Enabled = true,
        ScheduleType = AutomationScheduleType.Weekly,
        ExecuteTime = at,
        Weekdays = weekdays.ToList(),
    };

    [Fact]
    public void Daily_InterpretsExecuteTimeAsChinaWallClock()
    {
        // from = 2026-09-14 00:00 UTC = 中国 08:00（周一）。用户设 10:00 → 当次应为 UTC 02:00（中国当天 10:00），
        // 旧实现给出 UTC 10:00（中国当天 18:00，晚 8 小时）。
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Daily(new TimeOnly(10, 0)), from);
        Assert.Equal(new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_AlreadyFiredToday_PushesToTomorrow()
    {
        // from = 中国 10:00 已过（UTC 03:00 = 中国 11:00）→ 下回中国明天 10:00 = UTC 次日 02:00
        var from = new DateTime(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Daily(new TimeOnly(10, 0)), from);
        Assert.Equal(new DateTime(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Daily_MidnightEdgeIsNotYesterdays()
    {
        // 用户设 00:30（中国）。from = 中国周一 08:00（UTC 周一 00:00）→ 今天中国的 00:30 已在过去（=UTC 前日 16:30），
        // 下回 = 中国周二 00:30 = UTC 周一 16:30。
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Daily(new TimeOnly(0, 30)), from);
        Assert.Equal(new DateTime(2026, 9, 14, 16, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_MatchesChinaWeekday()
    {
        // from = 中国周一 08:00（UTC 周一 00:00）。选周二 10:00 → 中国周二 10:00 = UTC 周二 02:00。
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Weekly(new TimeOnly(10, 0), 2), from);
        Assert.Equal(new DateTime(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_NoWeekdays_TodaySlotStillAhead_RunsToday()
    {
        // 未选周几，但今天的中国 10:00（UTC 02:00）还没到 → 今天触发。
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Weekly(new TimeOnly(10, 0)), from);
        Assert.Equal(new DateTime(2026, 9, 14, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Weekly_NoWeekdays_TodaySlotPassed_FallsBackToNextWeek()
    {
        // 未选周几 + 今天的中国 10:00（UTC 02:00）已过（from = UTC 03:00）→ 下周同一天同一墙钟。
        var from = new DateTime(2026, 9, 14, 3, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(Weekly(new TimeOnly(10, 0)), from);
        Assert.Equal(new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Interval_IsPureElapsedTimeUntouched()
    {
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var next = AutomationScheduleCalculator.ComputeNextRun(new AutomationTask
        {
            Enabled = true,
            ScheduleType = AutomationScheduleType.Interval,
            IntervalMinutes = 30,
        }, from);
        Assert.Equal(from.AddMinutes(30), next);
    }
}
