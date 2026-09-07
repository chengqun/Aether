namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>
/// 透视结果：行维度列头（<see cref="RowHeaders"/>）+ 观测点列的去重值作为列头（<see cref="PivotHeaders"/>）
/// + 每行 = 行维度值数组 + 每个观测点头对应的聚合值。纯数据容器（零 Avalonia），由 <see cref="CsvPivot.Pivot"/> 产出。
/// </summary>
public sealed class PivotResult
{
    /// <summary>行维度列名。</summary>
    public IReadOnlyList<string> RowHeaders { get; }

    /// <summary>观测点列的去重值（转成列头，按数值自然序）。</summary>
    public IReadOnlyList<string> PivotHeaders { get; }

    /// <summary>透视行：每一行 = 行维度值数组 + 每个观测点头对应的值（与 <see cref="PivotHeaders"/> 对齐）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>值列名（单列模式 = 列名；比值模式 = "分子/分母"）。</summary>
    public string ValueHeader { get; }

    public PivotResult(
        IReadOnlyList<string> rowHeaders,
        IReadOnlyList<string> pivotHeaders,
        IReadOnlyList<IReadOnlyList<string>> rows,
        string valueHeader)
    {
        RowHeaders = rowHeaders;
        PivotHeaders = pivotHeaders;
        Rows = rows;
        ValueHeader = valueHeader;
    }

    /// <summary>完整列头（行维度 + 观测点），供表格渲染。</summary>
    public IReadOnlyList<string> AllHeaders => RowHeaders.Concat(PivotHeaders).ToArray();
}

/// <summary>
/// 值规格：透视单元格填什么。单列（<see cref="Single"/>=直接用某列聚合）或比值（<see cref="Ratio"/>=分子/分母，
/// 先各自求和再相除，得正确的比率聚合）。
/// </summary>
public readonly record struct CsvValueSpec(int NumeratorIndex, int? DenominatorIndex)
{
    /// <summary>单列：直接用该列（求和聚合）。</summary>
    public static CsvValueSpec Single(int columnIndex) => new(columnIndex, null);

    /// <summary>比值：分子列 ÷ 分母列（各求和后相除）。</summary>
    public static CsvValueSpec Ratio(int numeratorIndex, int denominatorIndex) => new(numeratorIndex, denominatorIndex);

    public bool IsRatio => DenominatorIndex is not null;
}

/// <summary>
/// CSV 透视（长表 → 宽表）：<paramref name="rowDimensionIndexes"/> 列去重组合成行，
/// <paramref name="pivotColumnIndex"/>（观测点列，如 obs_point 的 fpd1/fpd30/mob2）值去重转成列头（按数值自然序），
/// 单元格按 <paramref name="valueSpec"/> 聚合（单列求和 / 比值 = 分子求和 ÷ 分母求和）。
/// 纯函数、零依赖，可单测。用于 labs 知识对象页 csv 产物体文件的「透视表」视图。
/// </summary>
public static class CsvPivot
{
    public static PivotResult Pivot(CsvTable table, IReadOnlyList<int> rowDimensionIndexes, int pivotColumnIndex, CsvValueSpec valueSpec)
    {
        var colCount = table.ColumnCount;
        if (colCount == 0)
            return new PivotResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), string.Empty);

        pivotColumnIndex = Clamp(pivotColumnIndex, 0, colCount - 1);
        var numIdx = Clamp(valueSpec.NumeratorIndex, 0, colCount - 1);
        var denIdx = valueSpec.DenominatorIndex is { } d ? Clamp(d, 0, colCount - 1) : (int?)null;

        // 行维度（排除 pivot/value 列，保序）。
        var dims = rowDimensionIndexes
            .Where(i => i >= 0 && i < colCount && i != pivotColumnIndex && i != numIdx && i != denIdx)
            .Distinct()
            .ToArray();
        var rowHeaders = dims.Select(i => table.Headers[i]).ToArray();

        // 观测点列去重值（保首次出现序），按数值自然序排列（fpd1 < fpd3 < fpd10 < mob2 < mob12）。
        var pivotHeaders = table.Rows.Select(r => r[pivotColumnIndex]).Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal).ToList();
        NaturalSort(pivotHeaders);

        // 值列名。
        var valueHeader = valueSpec.IsRatio
            ? $"{table.Headers[numIdx]}/{table.Headers[denIdx!.Value]}"
            : table.Headers[numIdx];

        // 行维度组合 → 每观测点的聚合（求和）。
        var groups = new Dictionary<string, (string[] Dims, Dictionary<int, CellAggregate> ByPivot)>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var dimValues = dims.Select(i => row[i]).ToArray();
            var key = string.Join("", dimValues);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (dimValues, new Dictionary<int, CellAggregate>());
                groups[key] = group;
            }
            var pivotValue = row[pivotColumnIndex];
            var pivotIdx = pivotHeaders.IndexOf(pivotValue);
            if (pivotIdx < 0) continue;
            if (!group.ByPivot.TryGetValue(pivotIdx, out var agg))
            {
                agg = new CellAggregate();
                group.ByPivot[pivotIdx] = agg;
            }
            agg.AddNumerator(row[numIdx]);
            if (denIdx is { } di) agg.AddDenominator(row[di]);
        }

        var resultRows = new List<IReadOnlyList<string>>();
        foreach (var (dimValues, byPivot) in groups.Values)
        {
            var rowOut = new string[rowHeaders.Length + pivotHeaders.Count];
            for (var i = 0; i < dimValues.Length; i++) rowOut[i] = dimValues[i];
            foreach (var (pivotIdx, agg) in byPivot)
                rowOut[rowHeaders.Length + pivotIdx] = agg.Result(denIdx is not null);
            for (var i = 0; i < pivotHeaders.Count; i++)
                rowOut[rowHeaders.Length + i] ??= string.Empty;
            resultRows.Add(rowOut);
        }

        return new PivotResult(rowHeaders, pivotHeaders, resultRows, valueHeader);
    }

    /// <summary>智能分类整表，返回各分类列（供 VM 默认选 + UI 分组展示）。</summary>
    public static CsvFieldInfo[] Fields(CsvTable table) => CsvFieldClassifier.Classify(table).ToArray();

    /// <summary>观测点值自然排序：前缀（fpd/mob/字母）+ 数字，按「数字」升序；同数字稳定。就地排序。</summary>
    internal static void NaturalSort(List<string> values)
    {
        values.Sort((a, b) =>
        {
            var (pa, na) = SplitPrefixNumber(a);
            var (pb, nb) = SplitPrefixNumber(b);
            var cmp = string.CompareOrdinal(pa, pb);
            if (cmp != 0) return cmp;
            return na.CompareTo(nb);
        });
    }

    /// <summary>把 "fpd10"/"mob2" 拆成 (前缀 "fpd", 数字 10)；无数字则 (原串, 0)。</summary>
    private static (string Prefix, int Number) SplitPrefixNumber(string value)
    {
        var t = value.Trim();
        var i = 0;
        while (i < t.Length && !char.IsDigit(t[i])) i++;
        var prefix = i == 0 ? t : t[..i];
        var numberStr = i < t.Length ? t[i..] : string.Empty;
        var number = int.TryParse(numberStr, out var n) ? n : 0;
        return (prefix, number);
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    /// <summary>单元格聚合：单列=求和；比值=分子求和 ÷ 分母求和。</summary>
    private sealed class CellAggregate
    {
        private decimal _num;
        private decimal _den;
        private bool _hasNum;
        private bool _hasDen;
        private string _first = string.Empty;

        public void AddNumerator(string value)
        {
            var t = value?.Trim() ?? string.Empty;
            if (decimal.TryParse(t, out var d)) { _num += d; _hasNum = true; }
            else if (!_hasNum && _first.Length == 0) _first = t;
        }

        public void AddDenominator(string value)
        {
            var t = value?.Trim() ?? string.Empty;
            if (decimal.TryParse(t, out var d)) { _den += d; _hasDen = true; }
        }

        public string Result(bool isRatio)
        {
            if (isRatio) return _hasDen && _den != 0 ? (_num / _den).ToString("0.######") : string.Empty;
            return _hasNum ? _num.ToString("0.######") : _first;
        }
    }
}
