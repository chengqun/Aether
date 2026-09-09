using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>字段引用（拖拽数据载体）：名字 + 列索引 + 分类。可用于 Avalonia 拖拽数据。</summary>
public sealed record CsvFieldRef(string Name, int ColumnIndex, CsvFieldKind Kind);

/// <summary>字段列表里的可拖拽条目：普通字段（<see cref="Field"/> 非 null）或计算字段（<see cref="Formula"/> 非 null）。
/// 计算字段是用户新建的公式（如 [od_yi]/[obs_yi]），只能拖进「值」区。</summary>
public sealed record CsvFieldItem
{
    public string Name { get; }
    public CsvFieldRef? Field { get; }
    public string? Formula { get; }

    public bool IsCalculated => Formula is not null;

    private CsvFieldItem(string name, CsvFieldRef? field, string? formula)
    {
        Name = name;
        Field = field;
        Formula = formula;
    }

    public static CsvFieldItem Plain(CsvFieldRef field) => new(field.Name, field, null);
    public static CsvFieldItem Calculated(string name, string formula) => new(name, null, formula);
}

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

    /// <summary>左侧字段列表（未放置到任何槽位的普通字段；不含计算字段——见 <see cref="CalculatedFields"/>）。</summary>
    public ObservableCollection<CsvFieldItem> Fields { get; } = new();

    /// <summary>左侧「计算字段」区（用户新建的公式字段，如 [od_yi]/[obs_yi]；与普通字段分区显示）。</summary>
    public ObservableCollection<CsvFieldItem> CalculatedFields { get; } = new();

    /// <summary>已放置到「行维度」槽位的字段（多选）。</summary>
    public ObservableCollection<CsvFieldRef> RowDimensions { get; } = new();

    /// <summary>已放置到「列维度/观测点」槽位的字段（单选，可空）。</summary>
    [ObservableProperty] private CsvFieldRef? _columnDimension;

    /// <summary>值区的值字段（多槽：普通字段 或 计算字段公式）。</summary>
    public ObservableCollection<CsvValueSpec> ValueFields { get; } = new();

    /// <summary>筛选区的筛选字段（多字段，每个字段可勾选保留哪些值）。</summary>
    public ObservableCollection<CsvFilterField> FilterFields { get; } = new();

    /// <summary>布局变化（拖拽/百分比/筛选/计算字段增删）时触发——宿主经此自动保存布局。</summary>
    public event Action? LayoutChanged;

    /// <summary>是否正在应用布局（恢复时置 true，抑制 LayoutChanged 回写）。</summary>
    private bool _applyingLayout;

    /// <summary>源 CSV 行数/列数。</summary>
    public int SourceRowCount => _table.RowCount;
    public int SourceColumnCount => _table.ColumnCount;

    public CsvTableViewModel(string csvContent)
    {
        _table = CsvParser.Parse(csvContent);
        var classified = CsvPivot.Fields(_table);
        _allFields = classified.Select(f => new CsvFieldRef(f.Header, f.ColumnIndex, f.Kind)).ToArray();

        foreach (var f in _allFields) Fields.Add(CsvFieldItem.Plain(f));

        var dimensionFields = _allFields.Where(f => f.Kind == CsvFieldKind.Dimension).ToArray();
        var pivotFields = _allFields.Where(f => f.Kind == CsvFieldKind.Pivot).ToArray();

        // 行维度 = 全部维度字段。
        foreach (var f in dimensionFields) AssignToRowDimension(f);

        // 列维度 = 第一个观测点字段。
        if (pivotFields.Length > 0) ColumnDimension = TakeFieldFromList(pivotFields[0]);

        // 值区默认留空——原始字段全在字段列表，用户自己拖字段 + 新建计算字段。

        // 初始 = 详情表。
        _headers = _table.Headers;
        _rows = _table.Rows;
    }

    /// <summary>把普通字段从字段列表取出（默认归位用）。不在列表则直接返回。</summary>
    private CsvFieldRef? TakeFieldFromList(CsvFieldRef field)
    {
        if (field is null) return null;
        RemovePlainFromFields(field);
        return field;
    }

    private void RemovePlainFromFields(CsvFieldRef field)
    {
        var item = Fields.FirstOrDefault(i => i.Field is { } f && ReferenceEquals(f, field));
        if (item is not null) Fields.Remove(item);
    }

    private void AddPlainToFields(CsvFieldRef field)
    {
        if (!Fields.Any(i => i.Field is { } f && ReferenceEquals(f, field)))
            Fields.Add(CsvFieldItem.Plain(field));
    }

    /// <summary>把字段从所有槽位（含字段列表）移除——移动前的清理。</summary>
    private void DetachField(CsvFieldRef field)
    {
        RemovePlainFromFields(field);
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
        AddPlainToFields(field);
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
        if (ColumnDimension is { } old && !ReferenceEquals(old, field)) AddPlainToFields(old);
        DetachField(field);
        ColumnDimension = field;
        AfterSlotChange();
    }

    /// <summary>拖到「值」区（普通字段 → 加一个单列值字段）。</summary>
    public void AssignToValue(CsvFieldRef field)
    {
        if (field is null) return;
        RemovePlainFromFields(field);
        ValueFields.Add(CsvValueSpec.Single(field.ColumnIndex, field.Name));
        AfterSlotChange();
    }

    /// <summary>拖计算字段到「值」区（计算字段只能进值区）。</summary>
    public void AssignCalculatedToValue(CsvFieldItem item)
    {
        if (item is null || !item.IsCalculated || item.Formula is null) return;
        CalculatedFields.Remove(item);
        ValueFields.Add(CsvValueSpec.Formula_(item.Name, item.Formula));
        AfterSlotChange();
    }

    /// <summary>新建计算字段（命名 + 公式）→ 加进「计算字段」区（像普通字段一样，之后可拖进值区）。</summary>
    public void AddCalculatedField(string name, string formula)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(formula)) return;
        // 解析验证（失败则不添加）。
        try { CsvFormula.Parse(formula); }
        catch { return; }
        CalculatedFields.Add(CsvFieldItem.Calculated(name.Trim(), formula.Trim()));
        AfterSlotChange();
    }

    /// <summary>从值区移除一个值字段（普通字段弹回字段列表；计算字段弹回「计算字段」区）。</summary>
    public void RemoveValueField(CsvValueSpec spec)
    {
        if (spec is null) return;
        ValueFields.Remove(spec);
        if (spec.IsFormula)
        {
            if (!CalculatedFields.Any(i => i.Name == spec.Name))
                CalculatedFields.Add(CsvFieldItem.Calculated(spec.Name, spec.Formula!));
        }
        else
        {
            var ref_ = _allFields.FirstOrDefault(f => f.ColumnIndex == spec.ColumnIndex);
            if (ref_ is not null) AddPlainToFields(ref_);
        }
        AfterSlotChange();
    }

    /// <summary>把字段从行/列槽位移回字段列表（胶囊拖回列表）。</summary>
    public void RemoveField(CsvFieldRef field)
    {
        if (field is null) return;
        DetachField(field);
        AddPlainToFields(field);
        AfterSlotChange();
    }

    /// <summary>槽位变化后重建透视 + 触发布局变更（自动保存）。恢复布局期间（<see cref="_applyingLayout"/>）不触发。</summary>
    private void AfterSlotChange()
    {
        if (IsPivot) RebuildPivot();
        if (!_applyingLayout) LayoutChanged?.Invoke();
    }

    /// <summary>捕获当前布局快照（供持久化）。</summary>
    public CsvPivotLayout CaptureLayout()
    {
        var layout = new CsvPivotLayout
        {
            Rows = RowDimensions.Select(f => f.Name).ToList(),
            Column = ColumnDimension?.Name,
            Values = ValueFields.Select(v => new CsvValueLayout
            {
                Name = v.Name,
                Formula = v.Formula,
                AsPercent = v.AsPercent,
            }).ToList(),
            Filters = FilterFields.Select(f => new CsvFilterLayout
            {
                Field = f.Field.Name,
                Values = f.Allowed.Count == f.AllValues.Count ? null : f.Allowed.ToList(),
            }).ToList(),
            Calculated = CalculatedFields.Select(i => new CsvCalcLayout
            {
                Name = i.Name,
                Formula = i.Formula!,
            }).ToList(),
        };
        return layout;
    }

    /// <summary>应用布局快照（按字段名匹配；匹配不到的名字跳过）。清空当前槽位后套用。</summary>
    public void ApplyLayout(CsvPivotLayout? layout)
    {
        if (layout is null) return;
        _applyingLayout = true;
        try
        {
            // 清空所有槽位 + 字段列表，重填原始字段。
            RowDimensions.Clear();
            ColumnDimension = null;
            ValueFields.Clear();
            FilterFields.Clear();
            Fields.Clear();
            CalculatedFields.Clear();
            foreach (var f in _allFields) Fields.Add(CsvFieldItem.Plain(f));

            // 行维度（名字匹配）。
            foreach (var name in layout.Rows)
            {
                var f = FindField(name);
                if (f is not null) AssignToRowDimension(f);
            }
            // 列维度。
            if (layout.Column is not null && FindField(layout.Column) is { } col)
                ColumnDimension = TakeFieldFromList(col);
            // 值字段。
            foreach (var v in layout.Values)
            {
                if (v.Formula is { Length: > 0 } f)
                {
                    var spec = CsvValueSpec.Formula_(v.Name, f);
                    spec.AsPercent = v.AsPercent;
                    ValueFields.Add(spec);
                }
                else
                {
                    var field = FindField(v.Name);
                    if (field is not null)
                    {
                        RemovePlainFromFields(field);
                        var spec = CsvValueSpec.Single(field.ColumnIndex, field.Name);
                        spec.AsPercent = v.AsPercent;
                        ValueFields.Add(spec);
                    }
                }
            }
            // 筛选字段（勾选值）。
            foreach (var fl in layout.Filters)
            {
                var field = FindField(fl.Field);
                if (field is null) continue;
                RemovePlainFromFields(field);
                var values = _table.Rows.Select(r => r[field.ColumnIndex])
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
                var filter = new CsvFilterField(field, values);
                if (fl.Values is { Count: > 0 } allowed)
                {
                    filter.Allowed.Clear();
                    foreach (var v in filter.ValueOptions)
                        v.IsSelected = allowed.Contains(v.Value);
                    filter.RecomputeAllowed();
                    foreach (var v in allowed) if (values.Contains(v)) filter.Allowed.Add(v);
                }
                FilterFields.Add(filter);
            }
            // 计算字段区的字段（未拖进值的）。
            foreach (var c in layout.Calculated)
                CalculatedFields.Add(CsvFieldItem.Calculated(c.Name, c.Formula));
        }
        finally
        {
            _applyingLayout = false;
        }
        if (IsPivot) RebuildPivot();
    }

    /// <summary>按名字找字段（大小写不敏感，去 BOM）。</summary>
    private CsvFieldRef? FindField(string name)
        => _allFields.FirstOrDefault(f => string.Equals(f.Name.Trim('﻿'), name.Trim('﻿'), StringComparison.OrdinalIgnoreCase));

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

    private void RebuildPivot()
    {
        var rowDimIndexes = RowDimensions.Select(f => f.ColumnIndex).ToArray();
        var pivotIdx = ColumnDimension?.ColumnIndex ?? -1;
        if (pivotIdx < 0 || ValueFields.Count == 0) return;

        // 筛选：每个筛选字段 → (列索引, 允许值集合)。空集合 = 不过滤该字段。
        var filters = FilterFields
            .Select(f => (f.Field.ColumnIndex, (IReadOnlySet<string>)f.Allowed))
            .ToArray();

        var result = CsvPivot.Pivot(_table, rowDimIndexes, pivotIdx, ValueFields.ToArray(), filters);
        Headers = result.AllHeaders;
        Rows = result.Rows;
    }

    /// <summary>切换某值字段「显示为百分比」→ 重建。由 view（值字段设置弹窗）调用。</summary>
    public void ToggleValuePercent(CsvValueSpec spec) { if (IsPivot) RebuildPivot(); }
}
