namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>
/// 透视结果：行维度列头（<see cref="RowHeaders"/>）+ 透视列的去重值作为额外列头（<see cref="PivotHeaders"/>）
/// + 每行 = 行维度值数组 + 每个透视头对应的聚合值（<see cref="PivotValues"/>）。
/// 纯数据容器（零 Avalonia），由 <see cref="CsvPivot.Pivot"/> 产出。
/// </summary>
public sealed class PivotResult
{
    /// <summary>行维度列名（透视列 + 值列之外的所有列）。</summary>
    public IReadOnlyList<string> RowHeaders { get; }

    /// <summary>透视列的去重值（转成列头，按原表出现顺序）。</summary>
    public IReadOnlyList<string> PivotHeaders { get; }

    /// <summary>透视行：每一行 = 行维度值数组 + 每个透视头对应的值（与 <see cref="PivotHeaders"/> 对齐）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>值列名。</summary>
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

    /// <summary>完整列头（行维度 + 透视列），供表格渲染。</summary>
    public IReadOnlyList<string> AllHeaders => RowHeaders.Concat(PivotHeaders).ToArray();
}

/// <summary>
/// CSV 透视（长表 → 宽表）：把 <paramref name="pivotColumnIndex"/> 列的值去重转成列头，
/// 其余列（除 <paramref name="valueColumnIndex"/>）作为行维度去重组合，单元格填 <paramref name="valueColumnIndex"/> 的值。
/// 同一「行维度组合 + 透视值」有多行时，数值列求和、非数值取首个（不丢失信息但聚合数值语义）。
/// 纯函数、零依赖，可单测。用于 labs 知识对象页 csv 产物体文件的「透视表」视图。
/// </summary>
public static class CsvPivot
{
    public static PivotResult Pivot(CsvTable table, int pivotColumnIndex, int valueColumnIndex)
    {
        var colCount = table.ColumnCount;
        if (colCount == 0)
            return new PivotResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(), string.Empty);

        pivotColumnIndex = Clamp(pivotColumnIndex, 0, colCount - 1);
        valueColumnIndex = Clamp(valueColumnIndex, 0, colCount - 1);

        // 行维度列 = 除 pivot 列、value 列外的所有列（保持原顺序）。
        var rowDimIndexes = Enumerable.Range(0, colCount)
            .Where(i => i != pivotColumnIndex && i != valueColumnIndex)
            .ToArray();
        var rowHeaders = rowDimIndexes.Select(i => table.Headers[i]).ToArray();

        // 透视列去重值（保持原表出现顺序）。
        var pivotHeaders = new List<string>();
        var seenPivot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var pv = row[pivotColumnIndex];
            if (seenPivot.Add(pv)) pivotHeaders.Add(pv);
        }

        // 行维度组合 → 每透视头对应的值（求和聚合）。
        // key = 行维度值用不可见分隔符拼接（列值不含  的常见 CSV 安全）。
        var groups = new Dictionary<string, (string[] Dims, Dictionary<int, Aggregator> ByPivot)>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var dims = rowDimIndexes.Select(i => row[i]).ToArray();
            var key = string.Join("", dims);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (dims, new Dictionary<int, Aggregator>());
                groups[key] = group;
            }
            var pivotValue = row[pivotColumnIndex];
            var pivotIdx = pivotHeaders.IndexOf(pivotValue);
            if (pivotIdx < 0) continue;
            if (!group.ByPivot.TryGetValue(pivotIdx, out var agg))
            {
                agg = new Aggregator();
                group.ByPivot[pivotIdx] = agg;
            }
            agg.Add(row[valueColumnIndex]);
        }

        // 按首个出现的顺序输出行（Dictionary 保序在 .NET 下是插入序，但稳妥起见用列表）。
        var resultRows = new List<IReadOnlyList<string>>();
        foreach (var (dims, byPivot) in groups.Values)
        {
            var rowOut = new string[rowHeaders.Length + pivotHeaders.Count];
            for (var i = 0; i < dims.Length; i++) rowOut[i] = dims[i];
            foreach (var (pivotIdx, agg) in byPivot)
                rowOut[rowHeaders.Length + pivotIdx] = agg.Result;
            // 未出现的透视头填空串
            for (var i = 0; i < pivotHeaders.Count; i++)
                rowOut[rowHeaders.Length + i] ??= string.Empty;
            resultRows.Add(rowOut);
        }

        return new PivotResult(rowHeaders, pivotHeaders, resultRows, table.Headers[valueColumnIndex]);
    }

    /// <summary>
    /// 自动推荐透视列：找「去重值数量最少」的列（偏好枚举型维度列，如 obs_point 的 fpd1/fpd10）；
    /// 若表只有 1 列，返回其自身。用于透视表默认选中。
    /// </summary>
    public static int RecommendPivotColumn(CsvTable table)
    {
        if (table.ColumnCount <= 1) return 0;
        var best = 0;
        var bestDistinct = int.MaxValue;
        for (var i = 0; i < table.ColumnCount; i++)
        {
            var distinct = table.Rows.Select(r => r[i]).Distinct(StringComparer.Ordinal).Count();
            if (distinct < bestDistinct)
            {
                bestDistinct = distinct;
                best = i;
            }
        }
        return best;
    }

    /// <summary>推荐值列：第一个「多数值可解析为 decimal 且含小数点」的列（指标列通常是小数，如 od_yi=0.10；
    /// 维度列如月份 202501 虽可解析数值但无小数点，不入选）。无则返回 0。</summary>
    public static int RecommendValueColumn(CsvTable table)
    {
        for (var i = 0; i < table.ColumnCount; i++)
        {
            var fractional = 0;
            var total = 0;
            foreach (var row in table.Rows)
            {
                total++;
                var t = row[i].Trim();
                if (t.Length == 0) continue;
                if (decimal.TryParse(t, out _) && t.Contains('.')) fractional++;
            }
            if (total > 0 && fractional * 2 >= total) return i;
        }
        return 0;
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

    /// <summary>聚合：数值求和、非数值取首个（保序）。</summary>
    private sealed class Aggregator
    {
        private decimal _sum;
        private bool _hasNumeric;
        private string _first = string.Empty;

        public void Add(string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (decimal.TryParse(trimmed, out var d))
            {
                _hasNumeric = true;
                _sum += d;
                return;
            }
            // 非数值：取首个（保序，不覆盖）。
            if (!_hasNumeric && _first.Length == 0)
                _first = trimmed;
        }

        public string Result => _hasNumeric ? _sum.ToString("0.######") : _first;
    }
}
