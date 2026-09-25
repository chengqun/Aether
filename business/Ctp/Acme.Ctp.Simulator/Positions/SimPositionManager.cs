using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Ctp.Domain;

namespace Acme.Ctp.Simulator;

/// <summary>单个合约单个方向的持仓（今仓 + 昨仓 + 成本额 + 占用保证金额）。</summary>
public sealed class SimPositionLot
{
    /// <summary>今仓手数。</summary>
    public int Today { get; internal set; }
    /// <summary>昨仓手数。</summary>
    public int Yesterday { get; internal set; }
    /// <summary>成本额 Σ(开仓价 × 乘数 × 手数)——用来算加权均价与平仓盈亏。</summary>
    public double Cost { get; internal set; }
    /// <summary>占用保证金额 Σ(开仓时单手保证金 × 手数)——平仓按比例释放，全平后总额恰好归零。</summary>
    public double Margin { get; internal set; }

    public int Total => Today + Yesterday;

    /// <summary>加权开仓均价（无持仓时为 0）。</summary>
    public double AvgOpenPrice(SimInstrument instrument)
        => Total == 0 || instrument.VolumeMultiple <= 0 ? 0 : Cost / (instrument.VolumeMultiple * Total);
}

/// <summary>一个合约的双向持仓账本。</summary>
public sealed class SimPositionBook
{
    public SimPositionBook(string instrumentId, string exchangeId)
    {
        InstrumentId = instrumentId;
        ExchangeId = exchangeId;
    }

    public string InstrumentId { get; }
    public string ExchangeId { get; internal set; }

    /// <summary>多仓（买开形成的持仓）。</summary>
    public SimPositionLot Long { get; } = new();
    /// <summary>空仓（卖开形成的持仓）。</summary>
    public SimPositionLot Short { get; } = new();

    public SimPositionLot For(CtpPosiDirection direction)
        => direction == CtpPosiDirection.Long ? Long : Short;
}

/// <summary>平仓可用量计算结果。</summary>
public readonly record struct CloseAvailability(int Volume, int FromToday, int FromYesterday)
{
    public bool IsZero => Volume == 0;
}

/// <summary>
/// 持仓管理器：多空双向 × 今昨仓拆分，加权成本、保证金按比例释放。
/// <para>
/// 平仓顺序（与交易所口径一致）：
/// <list type="bullet">
/// <item><see cref="CtpOffsetFlag.Close"/>（通用平仓）：先今仓后昨仓（上期所外的交易所口径；上期所要求显式指定）。</item>
/// <item><see cref="CtpOffsetFlag.CloseToday"/>：只能平今仓。</item>
/// <item><see cref="CtpOffsetFlag.CloseYesterday"/>：只能平昨仓。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SimPositionManager
{
    private readonly Dictionary<string, SimPositionBook> _books = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有持仓账本（标的物排序稳定，便于断言）。</summary>
    public IEnumerable<SimPositionBook> Books => _books.Values.OrderBy(b => b.InstrumentId, StringComparer.Ordinal);

    public SimPositionBook GetOrCreate(string instrumentId, string exchangeId)
    {
        if (!_books.TryGetValue(instrumentId, out var book))
        {
            book = new SimPositionBook(instrumentId, exchangeId);
            _books[instrumentId] = book;
        }
        else if (!string.IsNullOrEmpty(exchangeId) && string.IsNullOrEmpty(book.ExchangeId))
        {
            book.ExchangeId = exchangeId;
        }
        return book;
    }

    public SimPositionBook? Find(string instrumentId)
        => _books.TryGetValue(instrumentId, out var b) ? b : null;

    /// <summary>
    /// 计算某方向（<paramref name="posiDirection"/> 为持仓方向）在给定开平标志下最多能平的手数。
    /// <paramref name="reserved"/> 为已被挂单中的平仓单占用的手数（防重复平仓）。
    /// </summary>
    public CloseAvailability AvailableToClose(string instrumentId, CtpPosiDirection posiDirection, CtpOffsetFlag offsetFlag, int reserved = 0)
    {
        var book = Find(instrumentId);
        if (book is null) return new CloseAvailability(0, 0, 0);
        var lot = book.For(posiDirection);

        // 先按标志算出「可动用的今/昨仓」，再扣掉挂单占用（占用先吃今仓，与下单时的判定一致）
        int today = offsetFlag switch
        {
            CtpOffsetFlag.CloseToday => lot.Today,
            CtpOffsetFlag.CloseYesterday => 0,
            _ => lot.Today,
        };
        int yesterday = offsetFlag switch
        {
            CtpOffsetFlag.CloseToday => 0,
            CtpOffsetFlag.CloseYesterday => lot.Yesterday,
            _ => lot.Yesterday,
        };
        var takeToday = Math.Max(0, today - Math.Min(reserved, today));
        var takeYd = Math.Max(0, yesterday - Math.Max(0, reserved - today));
        return new CloseAvailability(takeToday + takeYd, takeToday, takeYd);
    }

    /// <summary>开仓成交：把成交手数计入今仓，记录成本与占用保证金。</summary>
    public void ApplyOpenFill(string instrumentId, string exchangeId, CtpPosiDirection posiDirection,
        int volume, double price, SimInstrument instrument)
    {
        if (volume <= 0) return;
        var lot = GetOrCreate(instrumentId, exchangeId).For(posiDirection);
        lot.Today += volume;
        lot.Cost += price * instrument.VolumeMultiple * volume;
        lot.Margin += SimAccount.MarginOf(instrument, price, volume);
    }

    /// <summary>平仓成交结果（释放的保证金与盈亏，供资金账户记账）。</summary>
    public readonly record struct CloseFillResult(
        int Volume, int FromToday, int FromYesterday, double ReleasedMargin, double Pnl);

    /// <summary>
    /// 平仓成交：从今/昨仓扣减、按比例释放保证金、按加权成本算平仓盈亏。
    /// 调用方须先用 <see cref="AvailableToClose"/> 校验量。
    /// </summary>
    public CloseFillResult ApplyCloseFill(string instrumentId, CtpPosiDirection posiDirection, CtpOffsetFlag offsetFlag,
        int volume, double price, SimInstrument instrument)
    {
        if (volume <= 0) return new CloseFillResult(0, 0, 0, 0, 0);
        var book = Find(instrumentId);
        if (book is null) return new CloseFillResult(0, 0, 0, 0, 0);
        var lot = book.For(posiDirection);

        var totalBefore = lot.Total;
        if (totalBefore <= 0) return new CloseFillResult(0, 0, 0, 0, 0);

        // 今/昨拆分（CloseToday / CloseYesterday 各自受限）
        int fromToday = offsetFlag == CtpOffsetFlag.CloseYesterday ? 0 : Math.Min(volume, lot.Today);
        int fromYd = offsetFlag == CtpOffsetFlag.CloseToday
            ? 0
            : Math.Min(volume - fromToday, lot.Yesterday);
        var actual = fromToday + fromYd;
        if (actual <= 0) return new CloseFillResult(0, 0, 0, 0, 0);

        // 成本与保证金都按「占总量比例」释放——全平后两者恰好归零（资金守恒）
        var avgCost = lot.Cost / totalBefore;      // 单位：每手成本额（已含乘数）
        var releasedMargin = lot.Margin * actual / totalBefore;
        var pnl = posiDirection == CtpPosiDirection.Long
            ? (price * instrument.VolumeMultiple - avgCost) * actual
            : (avgCost - price * instrument.VolumeMultiple) * actual;

        lot.Today -= fromToday;
        lot.Yesterday -= fromYd;
        lot.Cost = Math.Max(0, lot.Cost - avgCost * actual);
        lot.Margin = Math.Max(0, lot.Margin - releasedMargin);
        if (lot.Total == 0) { lot.Cost = 0; lot.Margin = 0; }

        return new CloseFillResult(actual, fromToday, fromYd, releasedMargin, pnl);
    }

    /// <summary>按最新价重算全部持仓浮盈浮亏（写回资金账户）。</summary>
    public double RecomputePositionProfit(Func<string, double> lastPriceOf, Func<string, SimInstrument?> instrumentOf)
    {
        var total = 0.0;
        foreach (var book in _books.Values)
        {
            var instrument = instrumentOf(book.InstrumentId);
            if (instrument is null) continue;
            var last = lastPriceOf(book.InstrumentId);
            if (last <= 0) continue;
            total += Floating(book.Long, instrument, last, isLong: true);
            total += Floating(book.Short, instrument, last, isLong: false);
        }
        return SimAccount.Round(total);
    }

    private static double Floating(SimPositionLot lot, SimInstrument instrument, double last, bool isLong)
    {
        if (lot.Total == 0) return 0;
        var avg = lot.AvgOpenPrice(instrument);
        var diff = isLong ? last - avg : avg - last;
        return diff * instrument.VolumeMultiple * lot.Total;
    }

    /// <summary>镜像成 Domain 持仓列表：**每个合约每个方向一行**（今昨仓同时填，避免 UI 出现重复行）。</summary>
    /// <remarks>
    /// <para>
    /// <c>PosiDirection</c> 用 <see cref="CtpPosiDirection"/>：<c>Long='2'</c>/<c>Short='3'</c>。
    /// <b>注意</b>：<c>ctp_api.h</c> 里 <c>CtpPositionInfo.PosiDirection</c> 的注释写「'1' Long / '2' Short」，
    /// 与 <c>CtpEnums.cs</c> **冲突**——以后者为准（它是 Aether 侧对真柜台验证过的口径）。
    /// </para>
    /// <para><c>PositionDate</c>：有昨仓记 <c>History</c>，否则 <c>Today</c>——同样只是让字段看起来合理。</para>
    /// </remarks>
    public IReadOnlyList<CtpPosition> ToDto(string? instrumentId = null)
    {
        var list = new List<CtpPosition>();
        foreach (var book in Books)
        {
            if (!string.IsNullOrEmpty(instrumentId)
                && !string.Equals(book.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase))
                continue;

            Add(list, book, book.Long, CtpPosiDirection.Long);
            Add(list, book, book.Short, CtpPosiDirection.Short);
        }
        return list;

        static void Add(List<CtpPosition> list, SimPositionBook book, SimPositionLot lot, CtpPosiDirection dir)
        {
            if (lot.Total == 0) return;
            list.Add(new CtpPosition
            {
                InstrumentId = book.InstrumentId,
                ExchangeId = book.ExchangeId,
                PosiDirection = dir,
                HedgeFlag = CtpHedgeFlag.Speculation,
                PositionDate = lot.Yesterday > 0 ? CtpPositionDate.History : CtpPositionDate.Today,
                YdPosition = lot.Yesterday,
                TodayPosition = lot.Today,
                TotalPosition = lot.Total,
                UseMargin = SimAccount.Round(lot.Margin),
                PositionProfit = 0,   // 逐合约浮盈在 Account.PositionProfit 汇总，行情由真柜台提供故此处不另算
                CloseProfit = 0,
            });
        }
    }

    /// <summary>落盘快照（只为非零持仓生成行）。</summary>
    public IEnumerable<Storage.SimPositionSnapshot> ToSnapshots()
    {
        foreach (var book in Books)
        {
            foreach (var (lot, dir) in new[] { (book.Long, "L"), (book.Short, "S") })
            {
                if (lot.Total == 0) continue;
                yield return new Storage.SimPositionSnapshot(
                    book.InstrumentId, book.ExchangeId, dir, lot.Today, lot.Yesterday, lot.Cost, lot.Margin);
            }
        }
    }

    /// <summary>从快照恢复（覆盖式）。</summary>
    public void Restore(IEnumerable<Storage.SimPositionSnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {
            var lot = GetOrCreate(s.InstrumentId, s.ExchangeId)
                .For(s.Direction == "S" ? CtpPosiDirection.Short : CtpPosiDirection.Long);
            lot.Today = s.Today;
            lot.Yesterday = s.Yesterday;
            lot.Cost = s.Cost;
            lot.Margin = s.Margin;
        }
    }

    /// <summary>「昨仓」判定：把今仓并入昨仓——仿真不跨日，仅在显式调用时使用。</summary>
    public void RolloverToYesterday()
    {
        foreach (var book in _books.Values)
        {
            foreach (var lot in new[] { book.Long, book.Short })
            {
                lot.Yesterday += lot.Today;
                lot.Today = 0;
            }
        }
    }
}
