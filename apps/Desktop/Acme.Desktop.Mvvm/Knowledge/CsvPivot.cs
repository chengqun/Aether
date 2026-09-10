namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>值字段的汇总方式（Excel 透视表「值字段设置 → 汇总方式」）。</summary>
public enum CsvAggregation
{
    Sum = 0,
    Count = 1,
    Average = 2,
    Max = 3,
    Min = 4,
}

/// <summary>
/// 透视结果：行维度列头（<see cref="RowHeaders"/>）+ 值列头（每个值字段 × 每个观测点，<see cref="ValueHeaders"/>）
/// + 每行 = 行维度值数组 + 各值列的聚合值。纯数据容器（零 Avalonia），由 <see cref="CsvPivot.Pivot"/> 产出。
/// </summary>
public sealed class PivotResult
{
    /// <summary>行维度列名。</summary>
    public IReadOnlyList<string> RowHeaders { get; }

    /// <summary>观测点列的去重值（转成列头，按数值自然序）。</summary>
    public IReadOnlyList<string> PivotHeaders { get; }

    /// <summary>值列头（每个值字段 × 每个观测点；单值字段单观测点时 = 值字段名）。</summary>
    public IReadOnlyList<string> ValueHeaders { get; }

    /// <summary>透视行：每行 = 行维度值数组 + 各值列值（与 <see cref="ValueHeaders"/> 对齐）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public PivotResult(
        IReadOnlyList<string> rowHeaders,
        IReadOnlyList<string> pivotHeaders,
        IReadOnlyList<string> valueHeaders,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        RowHeaders = rowHeaders;
        PivotHeaders = pivotHeaders;
        ValueHeaders = valueHeaders;
        Rows = rows;
    }

    /// <summary>完整列头（行维度 + 值列），供表格渲染。</summary>
    public IReadOnlyList<string> AllHeaders => RowHeaders.Concat(ValueHeaders).ToArray();
}

/// <summary>
/// 值字段规格：单列（<see cref="Single"/>=聚合某列）或计算字段公式（<see cref="Formula"/>=字段表达式。
/// 聚合后求值：公式引用的各字段先在该格子求和，再代入公式，保证比率正确）。
/// 用「判别字段」而非 record 继承（避免派生 positional 属性与基类手写属性同名遮蔽 → 递归）。
/// </summary>
public sealed partial class CsvValueSpec : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>值字段显示名（列头）。</summary>
    public string Name { get; }

    /// <summary>单列：列索引；公式：-1。</summary>
    public int ColumnIndex { get; }

    /// <summary>公式表达式；单列 null。</summary>
    public string? Formula { get; }

    /// <summary>是否为公式（false = 单列）。</summary>
    public bool IsFormula => Formula is not null;

    /// <summary>该值字段是否按百分比显示（×100 加 %）。默认 false（原值）。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _asPercent;

    /// <summary>汇总方式（求和/计数/平均/最大/最小）。默认 Sum。仅单列值字段生效；
    /// 公式字段（聚合后求值）忽略此设置。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private CsvAggregation _aggregation = CsvAggregation.Sum;

    /// <summary>小数位数（null = 原样不截断；否则 Round 到该位数）。</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private int? _decimals;

    private CsvValueSpec(string name, int columnIndex, string? formula)
    {
        Name = name;
        ColumnIndex = columnIndex;
        Formula = formula;
    }

    /// <summary>单列：聚合该列（求和）。</summary>
    public static CsvValueSpec Single(int columnIndex, string columnName) => new(columnName, columnIndex, null);

    /// <summary>计算字段公式：命名 + 表达式（如 <c>[od_yi] / [obs_yi]</c>）。</summary>
    public static CsvValueSpec Formula_(string name, string formula) => new(name, -1, formula);
}

/// <summary>
/// CSV 透视（长表 → 宽表）：<paramref name="rowDimensionIndexes"/> 列去重组合成行，
/// <paramref name="pivotColumnIndex"/>（观测点列）值去重转成列头（按数值自然序），
/// 每个 <paramref name="valueSpecs"/> 值字段产出一组值列（单列求和 / 公式聚合后求值）。
/// <paramref name="asPercent"/> 为 true 时值列 ×100 加 %（比率展示）。纯函数、零依赖，可单测。
/// </summary>
public static class CsvPivot
{
    public static PivotResult Pivot(
        CsvTable table,
        IReadOnlyList<int> rowDimensionIndexes,
        int pivotColumnIndex,
        IReadOnlyList<CsvValueSpec> valueSpecs,
        IReadOnlyList<(int ColumnIndex, IReadOnlySet<string> Allowed)>? filters = null,
        bool showTotals = false)
    {
        var colCount = table.ColumnCount;
        if (colCount == 0)
            return new PivotResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        // 列维度（观测点）可选：无观测点列时退化为「仅按行维度分组」的普通聚合（每个值字段一列）。
        var hasPivot = pivotColumnIndex >= 0 && pivotColumnIndex < colCount;
        if (hasPivot)
            pivotColumnIndex = Clamp(pivotColumnIndex, 0, colCount - 1);
        valueSpecs = valueSpecs.Where(v => v != null).ToArray();
        if (valueSpecs.Count == 0)
            return new PivotResult(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        // 筛选谓词：行的筛选字段值必须都在允许集合内（空集合 = 全选，不过滤）。
        var filterList = filters?.Where(f => f.Allowed != null && f.Allowed.Count > 0)
            .Select(f => (ColumnIndex: f.ColumnIndex, Allowed: f.Allowed))
            .ToArray() ?? Array.Empty<(int ColumnIndex, IReadOnlySet<string> Allowed)>();

        // 行维度（排除 pivot 列 + 所有值字段引用的列，保序）。

        // 行维度（排除 pivot 列 + 所有值字段引用的列，保序）。
        var valueColumnIndexes = valueSpecs.SelectMany(v => v.IsFormula ? Array.Empty<int>() : new[] { v.ColumnIndex })
            .Where(i => i >= 0).ToHashSet();
        var dims = rowDimensionIndexes
            .Where(i => i >= 0 && i < colCount && i != pivotColumnIndex && !valueColumnIndexes.Contains(i))
            .Distinct()
            .ToArray();
        var rowHeaders = dims.Select(i => table.Headers[i]).ToArray();

        // 观测点列去重值（保首次序），数值自然序。无观测点列时退化为单占位槽（每值字段一列，不按观测点分列）。
        var pivotHeaders = hasPivot
            ? table.Rows.Select(r => r[pivotColumnIndex]).Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal).ToList()
            : new List<string> { string.Empty };
        if (hasPivot) NaturalSort(pivotHeaders);
        var multiPivot = hasPivot && pivotHeaders.Count > 1;

        // 每个值字段：解析一次 AST + 引用字段（公式）。
        var specMeta = new List<(CsvValueSpec Spec, CsvFormula.Node? Ast, string[] RefFields, int NumIdx, Dictionary<string, int> HeaderToIdx)>();
        foreach (var spec in valueSpecs)
        {
            string[] refFields = Array.Empty<string>();
            CsvFormula.Node? ast = null;
            if (spec.IsFormula && spec.Formula is { } formula)
            {
                ast = CsvFormula.Parse(formula);
                refFields = CsvFormula.ReferencedFields(ast).ToArray();
            }
            var headerToIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < table.Headers.Count; i++) headerToIdx[table.Headers[i]] = i;
            specMeta.Add((spec, ast, refFields, spec.ColumnIndex, headerToIdx));
        }

        // 值列头：每个值字段 × 每个观测点。
        var valueHeaders = new List<string>();
        foreach (var (spec, _, _, _, _) in specMeta)
        {
            if (!multiPivot) { valueHeaders.Add(spec.Name); }
            else { foreach (var ph in pivotHeaders) valueHeaders.Add($"{spec.Name} · {ph}"); }
        }

        // 分组：行维度组合 → 每 (值字段, 观测点) 的聚合。
        // 对公式字段，需对每个引用字段分别求和 → 用 Dictionary<string, decimal> per cell。
        // 同时维护「列总计」聚合（跨所有行维度组合），供总计行精确求和/计数/比率。
        var groups = new Dictionary<string, CellGroup>(StringComparer.Ordinal);
        var totals = new CellAggregate?[specMeta.Count, pivotHeaders.Count];
        foreach (var row in table.Rows)
        {
            // 筛选：任一筛选字段的值不在允许集合 → 丢弃该行。
            var filtered = false;
            foreach (var (fcol, allowed) in filterList)
            {
                if (fcol < 0 || fcol >= row.Count) continue;
                if (!allowed.Contains(row[fcol])) { filtered = true; break; }
            }
            if (filtered) continue;

            var dimValues = dims.Select(i => row[i]).ToArray();
            var key = string.Join("", dimValues);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new CellGroup(dimValues, specMeta.Count, pivotHeaders.Count);
                groups[key] = group;
            }
            // 无观测点列时恒取槽位 0（每值字段一列）；有观测点列则按其值定位槽位。
            var pivotIdx = hasPivot ? pivotHeaders.IndexOf(row[pivotColumnIndex]) : 0;
            if (pivotIdx < 0) continue;
            for (var si = 0; si < specMeta.Count; si++)
            {
                var (spec, _, refFields, numIdx, headerToIdx) = specMeta[si];
                var cell = group.Cells[si, pivotIdx] ??= new CellAggregate();
                var total = totals[si, pivotIdx] ??= new CellAggregate();
                if (spec.IsFormula)
                {
                    foreach (var rf in refFields)
                    {
                        if (headerToIdx.TryGetValue(rf, out var ci) && ci < row.Count)
                        {
                            cell.AddField(rf, row[ci]);
                            total.AddField(rf, row[ci]);
                        }
                    }
                }
                else
                {
                    cell.AddField(spec.Name, numIdx >= 0 && numIdx < row.Count ? row[numIdx] : string.Empty);
                    total.AddField(spec.Name, numIdx >= 0 && numIdx < row.Count ? row[numIdx] : string.Empty);
                }
            }
        }

        // 输出行。
        var resultRows = new List<IReadOnlyList<string>>();
        foreach (var group in groups.Values)
        {
            var rowOut = new string[rowHeaders.Length + valueHeaders.Count];
            for (var i = 0; i < group.Dims.Length; i++) rowOut[i] = group.Dims[i];
            var colOffset = rowHeaders.Length;
            for (var si = 0; si < specMeta.Count; si++)
            {
                var (spec, ast, _, _, _) = specMeta[si];
                var asPercent = spec.AsPercent;
                var decimals = spec.Decimals;
                for (var pi = 0; pi < pivotHeaders.Count; pi++)
                {
                    var cell = group.Cells[si, pi];
                    string text;
                    if (cell is null) text = string.Empty;
                    else if (spec.IsFormula && ast is not null)
                    {
                        var v = CsvFormula.Evaluate(ast, name => cell.GetField(name));
                        text = v is null ? string.Empty : FormatValue(v.Value, asPercent, decimals);
                    }
                    else
                    {
                        var v = cell.Aggregate(spec.Name, spec.Aggregation);
                        text = v is null ? string.Empty : FormatValue(v.Value, asPercent, decimals);
                    }
                    rowOut[colOffset] = text;
                    colOffset++;
                }
            }
            resultRows.Add(rowOut);
        }

        // 总计行：第一列填「总计」，其余行维度列空；值列用 totals 按同逻辑精确聚合（比率全局重算）。
        if (showTotals && resultRows.Count > 0)
        {
            var totalRow = new string[rowHeaders.Length + valueHeaders.Count];
            totalRow[0] = "总计";
            var tOffset = rowHeaders.Length;
            for (var si = 0; si < specMeta.Count; si++)
            {
                var (spec, ast, _, _, _) = specMeta[si];
                for (var pi = 0; pi < pivotHeaders.Count; pi++)
                {
                    var total = totals[si, pi];
                    string text;
                    if (total is null) text = string.Empty;
                    else if (spec.IsFormula && ast is not null)
                    {
                        var v = CsvFormula.Evaluate(ast, name => total.GetField(name));
                        text = v is null ? string.Empty : FormatValue(v.Value, spec.AsPercent, spec.Decimals);
                    }
                    else
                    {
                        var v = total.Aggregate(spec.Name, spec.Aggregation);
                        text = v is null ? string.Empty : FormatValue(v.Value, spec.AsPercent, spec.Decimals);
                    }
                    totalRow[tOffset] = text;
                    tOffset++;
                }
            }
            resultRows.Add(totalRow);
        }

        // 无观测点列时 PivotHeaders 对外为空（内部 pivotHeaders 含单占位槽仅作聚合用）。
        var publicPivotHeaders = hasPivot ? pivotHeaders : (IReadOnlyList<string>)Array.Empty<string>();
        return new PivotResult(rowHeaders, publicPivotHeaders, valueHeaders, resultRows);
    }

    private static string FormatValue(decimal v, bool asPercent, int? decimals)
    {
        var value = asPercent ? v * 100m : v;
        if (decimals is { } d)
        {
            value = Math.Round(value, d, MidpointRounding.AwayFromZero);
            return asPercent ? value.ToString("0." + new string('0', d)) + "%"
                             : value.ToString("0." + new string('0', d));
        }
        return asPercent ? value.ToString("0.###") + "%" : value.ToString("0.######");
    }

    /// <summary>智能分类整表。</summary>
    public static CsvFieldInfo[] Fields(CsvTable table) => CsvFieldClassifier.Classify(table).ToArray();

    /// <summary>观测点值自然排序：前缀 + 数字，按数字升序。就地排序。</summary>
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

    // —— 内部聚合容器 ——

    /// <summary>一个行维度组合：保存行维度值 + 每个 (值字段, 观测点) 的聚合 cell。</summary>
    private sealed class CellGroup
    {
        public string[] Dims { get; }
        public CellAggregate?[,] Cells { get; }

        public CellGroup(string[] dims, int specCount, int pivotCount)
        {
            Dims = dims;
            Cells = new CellAggregate?[specCount, pivotCount];
        }
    }

    /// <summary>一个格的聚合：对公式字段的各引用字段分别累加（正确比率）后，供公式求值。
    /// 同时保留 sum/count/max/min，供单列值字段按 <see cref="CsvAggregation"/> 取聚合结果。</summary>
    private sealed class CellAggregate
    {
        private readonly Dictionary<string, decimal> _sums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> _max = new(StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> _min = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hasNumeric = new(StringComparer.Ordinal);

        public void AddField(string fieldName, string value)
        {
            var t = value?.Trim() ?? string.Empty;
            if (decimal.TryParse(t, out var d))
            {
                _sums[fieldName] = _sums.TryGetValue(fieldName, out var s) ? s + d : d;
                _counts[fieldName] = _counts.TryGetValue(fieldName, out var c) ? c + 1 : 1;
                _max[fieldName] = _max.TryGetValue(fieldName, out var mx) ? Math.Max(mx, d) : d;
                _min[fieldName] = _min.TryGetValue(fieldName, out var mn) ? Math.Min(mn, d) : d;
                _hasNumeric.Add(fieldName);
                return;
            }
            // 非数值：计数（Count 聚合对文本也计数）；sum/max/min 不参与。
            _counts[fieldName] = _counts.TryGetValue(fieldName, out var c2) ? c2 + 1 : 1;
        }

        /// <summary>取某字段的求和值（无则 null）。公式求值用。</summary>
        public decimal? GetField(string fieldName)
            => _sums.TryGetValue(fieldName, out var v) ? v : null;

        /// <summary>按汇总方式取聚合结果。Count 对数值+文本都计数；其余仅数值（无数值返回 null）。</summary>
        public decimal? Aggregate(string fieldName, CsvAggregation agg)
        {
            switch (agg)
            {
                case CsvAggregation.Count:
                    return _counts.TryGetValue(fieldName, out var c) ? c : null;
                case CsvAggregation.Average:
                    return _sums.TryGetValue(fieldName, out var s) && _counts.TryGetValue(fieldName, out var ac) && ac > 0
                        ? s / ac : null;
                case CsvAggregation.Max:
                    return _max.TryGetValue(fieldName, out var mx2) ? mx2 : null;
                case CsvAggregation.Min:
                    return _min.TryGetValue(fieldName, out var mn2) ? mn2 : null;
                default: // Sum
                    return _sums.TryGetValue(fieldName, out var s2) ? s2 : null;
            }
        }
    }
}
