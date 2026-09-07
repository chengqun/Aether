using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>字段引用（拖拽数据载体）：名字 + 列索引 + 分类。可用于 Avalonia 拖拽数据（DataFormat&lt;CsvFieldRef&gt;）。</summary>
public sealed record CsvFieldRef(string Name, int ColumnIndex, CsvFieldKind Kind);

/// <summary>
/// CSV 产物体文件的表格 VM（UI-agnostic，零 Avalonia，但字段槽位模型可被拖拽 View 直接驱动）。
/// 解析 CSV 原文 → <see cref="CsvTable"/>，提供「详情表（原样）/ 透视表（长表转宽表）」两种视图切换。
/// 透视表字段用「槽位」分配（拖拽视图驱动）：行维度（多字段）、列维度/观测点（单字段）、值（分子/分母两槽）。
/// 智能字段分类（<see cref="CsvFieldClassifier"/>）设默认槽位：行维度=类目/日期列，观测点=obs_point，
/// 值=od_yi/obs_yi（逾期率）。
/// 数据以「<see cref="Headers"/> + <see cref="Rows"/>」扁平结构暴露给 view（ProDataGrid 经 LoadingRow 直接赋值）。
/// </summary>
public sealed partial class CsvTableViewModel : ObservableObject
{
    private readonly CsvTable _table;
    private readonly CsvFieldRef[] _allFields;

    /// <summary>当前视图的表头。</summary>
    [ObservableProperty] private IReadOnlyList<string> _headers = Array.Empty<string>();

    /// <summary>当前视图的行。</summary>
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<string>> _rows = Array.Empty<IReadOnlyList<string>>();

    /// <summary>是否透视表视图（false=详情表）。</summary>
    [ObservableProperty] private bool _isPivot;

    /// <summary>是否比值值列（true=两列相除，false=单列聚合）。</summary>
    [ObservableProperty] private bool _isRatioValue = true;

    /// <summary>左侧字段列表（未放置到任何槽位的字段）。</summary>
    public ObservableCollection<CsvFieldRef> Fields { get; } = new();

    /// <summary>已放置到「行维度」槽位的字段（多选）。</summary>
    public ObservableCollection<CsvFieldRef> RowDimensions { get; } = new();

    /// <summary>已放置到「列维度/观测点」槽位的字段（单选，可空）。</summary>
    [ObservableProperty] private CsvFieldRef? _columnDimension;

    /// <summary>值区「分子」槽位。</summary>
    [ObservableProperty] private CsvFieldRef? _valueNumerator;

    /// <summary>值区「分母」槽位（比值模式用）。</summary>
    [ObservableProperty] private CsvFieldRef? _valueDenominator;

    /// <summary>源 CSV 行数/列数。</summary>
    public int SourceRowCount => _table.RowCount;
    public int SourceColumnCount => _table.ColumnCount;

    public CsvTableViewModel(string csvContent)
    {
        _table = CsvParser.Parse(csvContent);
        var classified = CsvPivot.Fields(_table);
        _allFields = classified.Select(f => new CsvFieldRef(f.Header, f.ColumnIndex, f.Kind)).ToArray();

        // 初始：全部字段进字段列表；然后按分类归位到默认槽位。
        foreach (var f in _allFields) Fields.Add(f);

        // 分类归位。
        var dimensionFields = _allFields.Where(f => f.Kind == CsvFieldKind.Dimension).ToArray();
        var pivotFields = _allFields.Where(f => f.Kind == CsvFieldKind.Pivot).ToArray();
        var measureFields = _allFields.Where(f => f.Kind == CsvFieldKind.Measure).ToArray();

        // 行维度 = 全部维度字段。
        foreach (var f in dimensionFields) AssignToRowDimension(f);

        // 列维度 = 第一个观测点字段。
        if (pivotFields.Length > 0) ColumnDimension = TakeFieldFromList(pivotFields[0]);

        // 值 = od_yi / obs_yi（逾期率）。
        ValueNumerator = TakeFieldFromList(Prefer(measureFields, m =>
            string.Equals(m.Name, "od_yi", StringComparison.OrdinalIgnoreCase)
            || m.Name.ToLowerInvariant().Contains("od")
            || m.Name.Contains("逾期") || m.Name.Contains("坏账")));
        ValueDenominator = TakeFieldFromList(Prefer(measureFields, m =>
            string.Equals(m.Name, "obs_yi", StringComparison.OrdinalIgnoreCase)
            || m.Name.ToLowerInvariant().Contains("obs")
            || m.Name.Contains("观测")));

        // 初始 = 详情表。
        _headers = _table.Headers;
        _rows = _table.Rows;
    }

    private static CsvFieldRef Prefer(IReadOnlyList<CsvFieldRef> fields, Func<CsvFieldRef, bool> predicate)
    {
        foreach (var f in fields)
            if (predicate(f)) return f;
        return fields.Count > 0 ? fields[0] : null!;
    }

    /// <summary>把字段从字段列表取出（用于默认归位）；若该字段已在列表，移除。不在列表返回 null。</summary>
    private CsvFieldRef? TakeFieldFromList(CsvFieldRef field)
    {
        if (field is null) return null;
        if (Fields.Remove(field)) return field;
        return field; // 已在目标槽位（罕见），直接返回
    }

    // —— 拖拽移动（View 经这些方法把字段放入/移出槽位）——

    /// <summary>把字段从所有槽位（含字段列表）移除——移动前的清理。</summary>
    private void DetachField(CsvFieldRef field)
    {
        Fields.Remove(field);
        RowDimensions.Remove(field);
        if (ReferenceEquals(ColumnDimension, field)) ColumnDimension = null;
        if (ReferenceEquals(ValueNumerator, field)) ValueNumerator = null;
        if (ReferenceEquals(ValueDenominator, field)) ValueDenominator = null;
    }

    /// <summary>拖到「行维度」（多放，不可重复）。</summary>
    public void AssignToRowDimension(CsvFieldRef field)
    {
        if (field is null) return;
        DetachField(field);
        RowDimensions.Add(field);
        AfterSlotChange();
    }

    /// <summary>拖到「列维度/观测点」（单放，替换旧值，旧值弹回列表）。</summary>
    public void AssignToColumn(CsvFieldRef field)
    {
        if (field is null) return;
        if (ColumnDimension is { } old && !ReferenceEquals(old, field)) Fields.Add(old);
        DetachField(field);
        ColumnDimension = field;
        AfterSlotChange();
    }

    /// <summary>拖到值「分子」。</summary>
    public void AssignToNumerator(CsvFieldRef field) { AssignSingleValue(field, numerator: true); }

    /// <summary>拖到值「分母」。</summary>
    public void AssignToDenominator(CsvFieldRef field) { AssignSingleValue(field, numerator: false); }

    private void AssignSingleValue(CsvFieldRef field, bool numerator)
    {
        if (field is null) return;
        if (numerator && ValueNumerator is { } oldN && !ReferenceEquals(oldN, field)) Fields.Add(oldN);
        if (!numerator && ValueDenominator is { } oldD && !ReferenceEquals(oldD, field)) Fields.Add(oldD);
        DetachField(field);
        if (numerator) ValueNumerator = field; else ValueDenominator = field;
        AfterSlotChange();
    }

    /// <summary>把字段从槽位移回字段列表（胶囊上的 × 或拖回列表）。</summary>
    public void RemoveField(CsvFieldRef field)
    {
        if (field is null) return;
        DetachField(field);
        if (!Fields.Contains(field)) Fields.Add(field);
        AfterSlotChange();
    }

    private void AfterSlotChange() { if (IsPivot) RebuildPivot(); }

    /// <summary>切到详情表（原样展示）。</summary>
    [RelayCommand]
    private void SwitchToDetail()
    {
        IsPivot = false;
        Headers = _table.Headers;
        Rows = _table.Rows;
    }

    /// <summary>切到透视表（长表转宽表）。</summary>
    [RelayCommand]
    private void SwitchToPivot()
    {
        IsPivot = true;
        RebuildPivot();
    }

    partial void OnIsRatioValueChanged(bool value) { if (IsPivot) RebuildPivot(); }

    private void RebuildPivot()
    {
        var rowDimIndexes = RowDimensions.Select(f => f.ColumnIndex).ToArray();
        var pivotIdx = ColumnDimension?.ColumnIndex ?? -1;
        var numIdx = ValueNumerator?.ColumnIndex ?? -1;
        if (pivotIdx < 0 || numIdx < 0) return;

        var denIdx = ValueDenominator?.ColumnIndex ?? -1;
        var valueSpec = IsRatioValue && denIdx >= 0
            ? CsvValueSpec.Ratio(numIdx, denIdx)
            : CsvValueSpec.Single(numIdx);

        var result = CsvPivot.Pivot(_table, rowDimIndexes, pivotIdx, valueSpec);
        Headers = result.AllHeaders;
        Rows = result.Rows;
    }
}
