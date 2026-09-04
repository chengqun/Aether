using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>
/// CSV 产物体文件的表格 VM（UI-agnostic，零 Avalonia）。
/// 解析 CSV 原文 → <see cref="CsvTable"/>，提供「详情表（原样展示）/ 透视表（长表转宽表）」两种视图切换。
/// 详情表 = CSV 原样；透视表 = 选一个透视列（转列头）+ 一个值列（聚合填入），其余列作行维度。
/// 数据以「<see cref="Headers"/> + <see cref="Rows"/>」扁平结构暴露给 view（ProDataGrid 直接读，
/// 单元格文本经 LoadingRow 直接赋值，绕开 DataGrid 反射绑定）。
/// </summary>
public sealed partial class CsvTableViewModel : ObservableObject
{
    private readonly CsvTable _table;

    /// <summary>当前视图的表头（详情表=CSV 表头；透视表=行维度列 + 透视列去重值）。</summary>
    [ObservableProperty] private IReadOnlyList<string> _headers = Array.Empty<string>();

    /// <summary>当前视图的行（每行 = 与 <see cref="Headers"/> 等长的字符串数组）。</summary>
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<string>> _rows = Array.Empty<IReadOnlyList<string>>();

    /// <summary>是否透视表视图（false=详情表）。</summary>
    [ObservableProperty] private bool _isPivot;

    /// <summary>透视表：可选作「透视列（转列头）」的表头名列表。</summary>
    public IReadOnlyList<string> PivotColumnOptions { get; }

    /// <summary>透视表：可选作「值列（聚合填入）」的表头名列表。</summary>
    public IReadOnlyList<string> ValueColumnOptions { get; }

    /// <summary>当前选中的透视列名。</summary>
    [ObservableProperty] private string _selectedPivotColumn = string.Empty;

    /// <summary>当前选中的值列名。</summary>
    [ObservableProperty] private string _selectedValueColumn = string.Empty;

    /// <summary>源 CSV 行数/列数（供状态条显示「N 行 × M 列」）。</summary>
    public int SourceRowCount => _table.RowCount;
    public int SourceColumnCount => _table.ColumnCount;

    /// <summary>是否有透视表能力（列数 ≥ 2 才有意义转置）。列数 ≤ 1 时隐藏透视切换。</summary>
    public bool CanPivot => _table.ColumnCount >= 2;

    public CsvTableViewModel(string csvContent)
    {
        _table = CsvParser.Parse(csvContent);
        PivotColumnOptions = _table.Headers;
        ValueColumnOptions = _table.Headers;

        var pivotIdx = CsvPivot.RecommendPivotColumn(_table);
        var valueIdx = CsvPivot.RecommendValueColumn(_table);
        if (PivotColumnOptions.Count > 0)
        {
            _selectedPivotColumn = _table.Headers[pivotIdx];
            _selectedValueColumn = _table.Headers[valueIdx];
        }

        // 初始 = 详情表
        _headers = _table.Headers is string[] h ? h : _table.Headers.ToArray();
        _rows = _table.Rows;
    }

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
        if (!CanPivot) return;
        IsPivot = true;
        RebuildPivot();
    }

    /// <summary>透视列/值列变化时重算透视结果。</summary>
    partial void OnSelectedPivotColumnChanged(string value)
    {
        if (IsPivot) RebuildPivot();
    }

    partial void OnSelectedValueColumnChanged(string value)
    {
        if (IsPivot) RebuildPivot();
    }

    private void RebuildPivot()
    {
        var pivotIdx = IndexOf(_table.Headers, SelectedPivotColumn);
        var valueIdx = IndexOf(_table.Headers, SelectedValueColumn);
        if (pivotIdx < 0) pivotIdx = CsvPivot.RecommendPivotColumn(_table);
        if (valueIdx < 0) valueIdx = CsvPivot.RecommendValueColumn(_table);

        var result = CsvPivot.Pivot(_table, pivotIdx, valueIdx);
        Headers = result.AllHeaders;
        Rows = result.Rows;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }
}
