using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>字段引用（拖拽数据载体）：名字 + 列索引 + 分类。可用于 Avalonia 拖拽数据。</summary>
public sealed record CsvFieldRef(string Name, int ColumnIndex, CsvFieldKind Kind);

/// <summary>筛选字段：字段 + 该字段的可选值列表（勾选保留哪些值）。全部值去重后生成；
/// <see cref="Allowed"/> 为当前勾选的值（空集合 = 全选不过滤）。</summary>
public sealed partial class CsvFilterField : ObservableObject
{
    public CsvFieldRef Field { get; }

    /// <summary>该字段的全部去重值（勾选项候选）。</summary>
    public IReadOnlyList<string> AllValues { get; }

    /// <summary>值 → 是否勾选（保留）。</summary>
    public ObservableCollection<CsvFilterValueOption> ValueOptions { get; } = new();

    /// <summary>当前勾选的值集合（过滤用；空 = 全选不过滤）。供 Pivot 直接读。</summary>
    public HashSet<string> Allowed { get; } = new(StringComparer.Ordinal);

    public CsvFilterField(CsvFieldRef field, IReadOnlyList<string> allValues)
    {
        Field = field;
        AllValues = allValues;
        foreach (var v in allValues)
        {
            Allowed.Add(v);
            ValueOptions.Add(new CsvFilterValueOption(v, isSelected: true));
        }
    }

    /// <summary>勾选变化 → 重算 <see cref="Allowed"/>。由 view/选项触发。</summary>
    public void RecomputeAllowed()
    {
        Allowed.Clear();
        foreach (var o in ValueOptions)
            if (o.IsSelected) Allowed.Add(o.Value);
    }
}

/// <summary>筛选字段的一个值选项（勾选 = 保留该值）。</summary>
public sealed partial class CsvFilterValueOption : ObservableObject
{
    public string Value { get; }

    [ObservableProperty] private bool _isSelected;

    public CsvFilterValueOption(string value, bool isSelected)
    {
        Value = value;
        _isSelected = isSelected;
    }
}

/// <summary>
/// CSV 产物体文件的表格 VM（UI-agnostic，零 Avalonia，但字段槽位模型可被拖拽 View 直接驱动）。
/// 解析 CSV 原文 → <see cref="CsvTable"/>，提供「详情表（原样）/ 透视表（长表转宽表）」两种视图切换。
/// 透视表字段用「槽位」分配（拖拽视图驱动）：行维度（多字段）、列维度/观测点（单字段）、
/// 值（多值字段：普通字段单列求和 或 计算字段公式）。百分比开关控制值列 ×100 加 %。
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

    /// <summary>值列是否按百分比显示（×100 加 %）；false = 原值。</summary>
    [ObservableProperty] private bool _isPercentValue = true;

    /// <summary>左侧字段列表（未放置到任何槽位的字段）。</summary>
    public ObservableCollection<CsvFieldRef> Fields { get; } = new();

    /// <summary>已放置到「行维度」槽位的字段（多选）。</summary>
    public ObservableCollection<CsvFieldRef> RowDimensions { get; } = new();

    /// <summary>已放置到「列维度/观测点」槽位的字段（单选，可空）。</summary>
    [ObservableProperty] private CsvFieldRef? _columnDimension;

    /// <summary>值区的值字段（多槽：普通字段 或 计算字段公式）。</summary>
    public ObservableCollection<CsvValueSpec> ValueFields { get; } = new();

    /// <summary>筛选区的筛选字段（多字段，每个字段可勾选保留哪些值）。</summary>
    public ObservableCollection<CsvFilterField> FilterFields { get; } = new();

    /// <summary>源 CSV 行数/列数。</summary>
    public int SourceRowCount => _table.RowCount;
    public int SourceColumnCount => _table.ColumnCount;

    public CsvTableViewModel(string csvContent)
    {
        _table = CsvParser.Parse(csvContent);
        var classified = CsvPivot.Fields(_table);
        _allFields = classified.Select(f => new CsvFieldRef(f.Header, f.ColumnIndex, f.Kind)).ToArray();

        foreach (var f in _allFields) Fields.Add(f);

        var dimensionFields = _allFields.Where(f => f.Kind == CsvFieldKind.Dimension).ToArray();
        var pivotFields = _allFields.Where(f => f.Kind == CsvFieldKind.Pivot).ToArray();
        var measureFields = _allFields.Where(f => f.Kind == CsvFieldKind.Measure).ToArray();

        // 行维度 = 全部维度字段。
        foreach (var f in dimensionFields) AssignToRowDimension(f);

        // 列维度 = 第一个观测点字段。
        if (pivotFields.Length > 0) ColumnDimension = TakeFieldFromList(pivotFields[0]);

        // 值 = 默认计算字段「逾期率」= [od_yi] / [obs_yi]（若两字段都在）。
        var od = measureFields.FirstOrDefault(m => m.Name.Equals("od_yi", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("od") || m.Name.Contains("逾期") || m.Name.Contains("坏账"));
        var obs = measureFields.FirstOrDefault(m => m.Name.Equals("obs_yi", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("obs") || m.Name.Contains("观测"));
        if (od is not null && obs is not null && !ReferenceEquals(od, obs))
        {
            TakeFieldFromList(od);
            TakeFieldFromList(obs);
            ValueFields.Add(CsvValueSpec.Formula_("逾期率", $"[{od.Name}] / [{obs.Name}]"));
        }
        else if (measureFields.Length > 0)
        {
            // 无 od/obs 两列 → 退回单列（第一个指标）。
            TakeFieldFromList(measureFields[0]);
            ValueFields.Add(CsvValueSpec.Single(measureFields[0].ColumnIndex, measureFields[0].Name));
        }

        // 初始 = 详情表。
        _headers = _table.Headers;
        _rows = _table.Rows;
    }

    /// <summary>把字段从字段列表取出（默认归位用）。不在列表则直接返回。</summary>
    private CsvFieldRef? TakeFieldFromList(CsvFieldRef field)
    {
        if (field is null) return null;
        Fields.Remove(field);
        return field;
    }

    /// <summary>把字段从所有槽位（含字段列表）移除——移动前的清理。</summary>
    private void DetachField(CsvFieldRef field)
    {
        Fields.Remove(field);
        RowDimensions.Remove(field);
        if (ReferenceEquals(ColumnDimension, field)) ColumnDimension = null;
        RemoveFilter(field, rebuild: false);
    }

    // —— 拖拽移动（View 经这些方法把字段放入/移出槽位）——

    /// <summary>拖到「行维度」（多放）。</summary>
    public void AssignToRowDimension(CsvFieldRef field)
    {
        if (field is null) return;
        DetachField(field);
        RowDimensions.Add(field);
        AfterSlotChange();
    }

    /// <summary>拖到「筛选」区（该字段作为过滤条件，初始全选其全部值）。</summary>
    public void AssignToFilter(CsvFieldRef field)
    {
        if (field is null) return;
        DetachField(field);
        // 取该字段全部去重值（保序）。
        var values = _table.Rows.Select(r => r[field.ColumnIndex])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal).ToArray();
        FilterFields.Add(new CsvFilterField(field, values));
        AfterSlotChange();
    }

    /// <summary>从筛选区移除筛选字段（字段弹回列表）。</summary>
    public void RemoveFilter(CsvFieldRef field, bool rebuild = true)
    {
        var existing = FilterFields.FirstOrDefault(f => ReferenceEquals(f.Field, field));
        if (existing is null) return;
        FilterFields.Remove(existing);
        if (!Fields.Contains(field)) Fields.Add(field);
        if (rebuild) AfterSlotChange();
    }

    /// <summary>切换筛选字段的某值勾选 → 重算允许集合 + 重建。</summary>
    public void ToggleFilterValue(CsvFilterField filter)
    {
        filter.RecomputeAllowed();
        AfterSlotChange();
    }

    /// <summary>拖到「列维度/观测点」（单放替换，旧值弹回列表）。</summary>
    public void AssignToColumn(CsvFieldRef field)
    {
        if (field is null) return;
        if (ColumnDimension is { } old && !ReferenceEquals(old, field)) Fields.Add(old);
        DetachField(field);
        ColumnDimension = field;
        AfterSlotChange();
    }

    /// <summary>拖到「值」区（普通字段 → 加一个单列值字段）。</summary>
    public void AssignToValue(CsvFieldRef field)
    {
        if (field is null) return;
        Fields.Remove(field);
        ValueFields.Add(CsvValueSpec.Single(field.ColumnIndex, field.Name));
        AfterSlotChange();
    }

    /// <summary>新建计算字段（命名 + 公式）加入值区。</summary>
    public void AddCalculatedField(string name, string formula)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(formula)) return;
        // 解析验证（失败则不添加，状态留空）。
        try { CsvFormula.Parse(formula); }
        catch { return; }
        ValueFields.Add(CsvValueSpec.Formula_(name.Trim(), formula.Trim()));
        AfterSlotChange();
    }

    /// <summary>从值区移除一个值字段（普通字段弹回字段列表；计算字段直接丢弃）。</summary>
    public void RemoveValueField(CsvValueSpec spec)
    {
        if (spec is null) return;
        ValueFields.Remove(spec);
        if (!spec.IsFormula)
        {
            var ref_ = _allFields.FirstOrDefault(f => f.ColumnIndex == spec.ColumnIndex);
            if (ref_ is not null && !Fields.Contains(ref_)) Fields.Add(ref_);
        }
        AfterSlotChange();
    }

    /// <summary>把字段从行/列槽位移回字段列表（胶囊拖回列表）。</summary>
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

    partial void OnIsPercentValueChanged(bool value) { if (IsPivot) RebuildPivot(); }

    private void RebuildPivot()
    {
        var rowDimIndexes = RowDimensions.Select(f => f.ColumnIndex).ToArray();
        var pivotIdx = ColumnDimension?.ColumnIndex ?? -1;
        if (pivotIdx < 0 || ValueFields.Count == 0) return;

        // 筛选：每个筛选字段 → (列索引, 允许值集合)。空集合 = 不过滤该字段。
        var filters = FilterFields
            .Select(f => (f.Field.ColumnIndex, (IReadOnlySet<string>)f.Allowed))
            .ToArray();

        var result = CsvPivot.Pivot(_table, rowDimIndexes, pivotIdx, ValueFields.ToArray(), IsPercentValue, filters);
        Headers = result.AllHeaders;
        Rows = result.Rows;
    }
}
