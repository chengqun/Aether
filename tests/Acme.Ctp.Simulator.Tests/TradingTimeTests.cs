using System;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator.Tests;

/// <summary>
/// 交易时段表（<see cref="CtpTradingCalendar.IsTradingTime"/>）与交易日推导（<see cref="SimClock"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 仿真柜台默认不挑时段（<see cref="SimOptions.RejectOrdersOutsideTradingTime"/> = false，
/// <see cref="SimOptions.GateByTradingTime"/> = false）——离线回放要的就是随时可跑。
/// 但时段表**仍必须正确**：它是「重连还是等开盘」的判据，也是开启闸门后唯一的准入依据。
/// </para>
/// <para>
/// 口径是**宽并集**（不按品种分级），且**无节假日表**——法定假日返回 true 是已知取舍（见类型注释）。
/// 本文件把这些边界逐条钉死，防后续「顺手收紧」把某个真实交易时段关掉。
/// </para>
/// <para>锚定日历：2026-09-23 周三 / 09-26 周六 / 09-27 周日 / 09-28 周一 / 10-01 周四（国庆，无假日表）。</para>
/// </remarks>
public class TradingTimeTests
{
    private static bool At(int year, int month, int day, int hour, int minute, int second = 0)
        => CtpTradingCalendar.IsTradingTime(new DateTime(year, month, day, hour, minute, second));

    // —— 日盘边界 ——

    [Fact]
    public void 日盘三段与休盘间隔的边界()
    {
        // 09:00–10:15
        Assert.True(At(2026, 9, 23, 9, 0));
        Assert.True(At(2026, 9, 23, 10, 14, 59));
        Assert.False(At(2026, 9, 23, 10, 15));      // 休盘开始（含端点）
        Assert.False(At(2026, 9, 23, 10, 29, 59));  // 休盘期间

        // 10:30–11:30
        Assert.True(At(2026, 9, 23, 10, 30));
        Assert.True(At(2026, 9, 23, 11, 29, 59));
        Assert.False(At(2026, 9, 23, 11, 30));

        // 午休
        Assert.False(At(2026, 9, 23, 12, 0));
        Assert.False(At(2026, 9, 23, 13, 29, 59));

        // 13:30–15:00
        Assert.True(At(2026, 9, 23, 13, 30));
        Assert.True(At(2026, 9, 23, 14, 59, 59));
        Assert.False(At(2026, 9, 23, 15, 0));
    }

    [Fact]
    public void 日盘前后与傍晚均非交易时段()
    {
        Assert.False(At(2026, 9, 23, 8, 59, 59));
        Assert.False(At(2026, 9, 23, 15, 30));
        Assert.False(At(2026, 9, 23, 20, 59, 59)); // 夜盘开盘前最后一秒
    }

    // —— 夜盘（跨午夜） ——

    [Fact]
    public void 夜盘开盘与收盘的边界()
    {
        // 21:00–23:59:59.999（当日为周一时也算——周一晚有夜盘）
        Assert.True(At(2026, 9, 23, 21, 0));
        Assert.True(At(2026, 9, 23, 23, 59, 59));

        // 00:00–02:30（承接前一夜盘）——02:30 为收盘不含
        Assert.True(At(2026, 9, 24, 0, 0));
        Assert.True(At(2026, 9, 24, 2, 29, 59));
        Assert.False(At(2026, 9, 24, 2, 30));
        Assert.False(At(2026, 9, 24, 3, 0));
    }

    [Fact]
    public void 周五夜盘延续到周六凌晨而周日无夜盘()
    {
        // 周五 23:00 有夜盘
        Assert.True(At(2026, 9, 25, 23, 0));
        // 周六凌晨 00:30 是周五夜盘的延续 ⇒ 仍在交易时段
        Assert.True(At(2026, 9, 26, 0, 30));

        // 周日 23:00 / 周一凌晨 00:30 都没有夜盘（周日晚不开盘）
        Assert.False(At(2026, 9, 27, 23, 0));
        Assert.False(At(2026, 9, 28, 0, 30));

        // 周一晚 21:00 恢复
        Assert.True(At(2026, 9, 28, 21, 0));
    }

    [Fact]
    public void 周末日盘休市()
    {
        Assert.False(At(2026, 9, 26, 10, 0)); // 周六
        Assert.False(At(2026, 9, 27, 10, 0)); // 周日
        Assert.False(At(2026, 9, 26, 21, 0)); // 周六晚
    }

    [Fact]
    public void 不含节假日表故国庆仍判为交易时段()
    {
        // 2026-10-01 周四：真实交易所休市，但本表**不含假日表** ⇒ 返回 true。
        // 这是刻意的取舍（见 CtpTradingCalendar 类型注释）：假日里重连会失败并退避，不会崩。
        // 若将来加了假日表，本断言须随之改为 False——留着就是为了让那次改动必须是有意识的。
        Assert.True(At(2026, 10, 1, 10, 0));
    }

    // —— 时区 ——

    [Fact]
    public void UTC时间先转本地再判定()
    {
        // 取一个本地交易时刻往返成 UTC 输入——无论测试机在哪个时区，判定都应与本地输入一致
        var local = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Local);
        Assert.True(CtpTradingCalendar.IsTradingTime(local.ToUniversalTime()));

        var outside = new DateTime(2026, 9, 23, 3, 0, 0, DateTimeKind.Local); // 凌晨闭市
        Assert.False(CtpTradingCalendar.IsTradingTime(outside.ToUniversalTime()));
    }

    [Fact]
    public void 夹具时钟落在交易时段内()
    {
        // 单测夹具选的时刻必须真的在盘中——否则所有用它的用例都在「闭市」假设下跑
        Assert.True(CtpTradingCalendar.IsTradingTime(SimHarness.TradingNow));
    }

    // —— 交易日推导（SimClock） ——

    [Fact]
    public void 日间时钟的交易日即当日()
    {
        var clock = new SimClock(() => new DateTime(2026, 9, 23, 10, 0, 0));

        Assert.Equal("20260923", clock.TradingDay);
        Assert.Equal("10:00:00", clock.TimeOfDay);
    }

    [Fact]
    public void 夜盘归属下一工作日()
    {
        // 周三晚 → 周四
        Assert.Equal("20260924", new SimClock(() => new DateTime(2026, 9, 23, 21, 30, 0)).TradingDay);

        // 周五晚 → 跳过周六周日 ⇒ 下周一
        Assert.Equal("20260928", new SimClock(() => new DateTime(2026, 9, 25, 21, 30, 0)).TradingDay);
    }

    [Fact]
    public void 凌晨的交易日取当日日期而非再往后推()
    {
        // 02:30 前的凌晨属于「已经算过的那一日」——不能再 +1 天，
        // 否则每个夜盘日都会多推一日（交易日字段整体漂移）。
        Assert.Equal("20260924", new SimClock(() => new DateTime(2026, 9, 24, 1, 0, 0)).TradingDay);

        // 已知简化：周六凌晨理论上应属下一交易日（周一），而本实现取当日日期（周六）。
        // 仿真不做结算，只求字段「看起来合理」；这条断言把这个已知缺口显式记录下来。
        Assert.Equal("20260926", new SimClock(() => new DateTime(2026, 9, 26, 1, 0, 0)).TradingDay);
    }

    [Fact]
    public void 时间字符串格式与毫秒取值()
    {
        var clock = new SimClock(() => new DateTime(2026, 9, 23, 14, 5, 7, 123));

        Assert.Equal("14:05:07", clock.TimeOfDay);
        Assert.Equal("14:05:07", clock.TickTime);
        Assert.Equal(123, clock.Milliseconds);
    }
}
