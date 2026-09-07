namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>列的分类（透视语义）。</summary>
public enum CsvFieldKind
{
    /// <summary>行维度（类目/日期列，如 grp/product_flag3/m）。</summary>
    Dimension,

    /// <summary>观测点列（值形如 fpd1/fpd30/mob2 的枚举，转成列头）。</summary>
    Pivot,

    /// <summary>数值指标列（如 od_yi 坏账率 / putout_cnt / amt_yi）。</summary>
    Measure,
}

/// <summary>一列的分类结果。</summary>
public readonly record struct CsvFieldInfo(CsvFieldKind Kind, string Header, int ColumnIndex);

/// <summary>
/// CSV 字段智能分类器：按列名 + 值形态启发式，把每列分成 <see cref="CsvFieldKind"/> 四类，
/// 供透视表默认选「行维度 / 观测点列 / 值列」。纯函数、零依赖，可单测。
/// </summary>
public static class CsvFieldClassifier
{
    /// <summary>观测点列名信号（英/中：obs_point、fpd、mob、期/账期/观测）。</summary>
    private static readonly string[] PivotNameHints = { "obs_point", "obs", "fpd", "mob", "期", "账龄", "观测", "bucket", "bin" };

    /// <summary>数值指标列名信号（逾期/坏账/金额/笔数/率）。</summary>
    private static readonly string[] MeasureNameHints = { "od", "obs", "amt", "cnt", "rate", "ratio", "yi", "num", "count", "val", "逾期", "坏账", "金额", "笔数", "率", "不良" };

    /// <summary>日期/月份维度列名信号。</summary>
    private static readonly string[] DateNameHints = { "m", "date", "month", "year", "time", "period", "月份", "日期", "月" };

    public static IReadOnlyList<CsvFieldInfo> Classify(CsvTable table)
    {
        var result = new List<CsvFieldInfo>(table.ColumnCount);
        for (var i = 0; i < table.ColumnCount; i++)
        {
            var header = table.Headers[i];
            var kind = ClassifyColumn(table, i, header);
            result.Add(new CsvFieldInfo(kind, header, i));
        }
        return result;
    }

    private static CsvFieldKind ClassifyColumn(CsvTable table, int columnIndex, string header)
    {
        var lower = header.ToLowerInvariant();
        var values = table.Rows.Select(r => r[columnIndex]).ToArray();
        var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();

        // 观测点列：列名含 obs_point/fpd/mob 等，且值形如「前缀+数字」的枚举（fpd1/fpd30/mob2）。
        if (PivotNameHints.Any(h => lower.Contains(h)) && LooksLikeBinnedEnum(distinct))
            return CsvFieldKind.Pivot;

        // 观测点列（无名称信号）：值全部匹配「字母/下划线 + 数字」，且去重数 2~40 的枚举。
        if (LooksLikeBinnedEnum(distinct) && distinct.Length is >= 2 and <= 40)
            return CsvFieldKind.Pivot;

        // 数值指标：列名含强指标信号（od/obs/逾期/坏账/金额/笔数/率）→ 直接判指标（不依赖值形态，
        // 因数据可能暂时为空，如 od_yi 某列全空）。
        if (MeasureNameHints.Any(h => lower.Contains(h)))
            return CsvFieldKind.Measure;
        // 无列名信号 → 靠值形态：多数值 + 含小数点。
        if (IsMostlyNumeric(values) && HasAnyFraction(values))
            return CsvFieldKind.Measure;

        // 其余：行维度（类目/日期列）。
        return CsvFieldKind.Dimension;
    }

    /// <summary>枚举值是否全部形如「前缀+数字」（fpd1/fpd30/mob10/202501 也符合，但日期列已由列名信号区分）。
    /// 空集合返回 false。</summary>
    private static bool LooksLikeBinnedEnum(IReadOnlyList<string> distinct)
    {
        if (distinct.Count == 0) return false;
        foreach (var v in distinct)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var t = v.Trim();
            // 前缀（字母/下划线/中文）+ 末段纯数字。fpd10 / mob2 / 01.借条（含点，非纯前缀+数字）
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\p{L}_]*\d+$")) return false;
        }
        return true;
    }

    /// <summary>多数值（≥ 2/3）可解析为数字。</summary>
    private static bool IsMostlyNumeric(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return false;
        var numeric = 0;
        var nonEmpty = 0;
        foreach (var v in values)
        {
            var t = v.Trim();
            if (t.Length == 0) continue;
            nonEmpty++;
            if (decimal.TryParse(t, out _)) numeric++;
        }
        return nonEmpty > 0 && numeric * 2 >= nonEmpty;
    }

    /// <summary>是否存在含小数点的数值（区分「整数值维度」如月份 202501 vs 指标 0.10）。</summary>
    private static bool HasAnyFraction(IReadOnlyList<string> values)
    {
        foreach (var v in values)
        {
            var t = v.Trim();
            if (t.Length == 0) continue;
            if (decimal.TryParse(t, out _) && t.Contains('.')) return true;
        }
        return false;
    }
}
