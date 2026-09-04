using System;
using System.Collections.Generic;
using System.ComponentModel;
using Acme.Desktop.Mvvm.Knowledge;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 自定义 CSV 列：override <see cref="DataGridTextColumn.GenerateElement"/> 直接从行数据
/// （<c>IReadOnlyList&lt;string&gt;</c>）按 <see cref="ColumnIndex"/> 取文本赋给 TextBlock——<b>零绑定、零反射</b>。
/// 绕开 Avalonia DataGrid 反射绑定单元格（<c>Cells[idx]</c>）在本仓不渲染的历史坑（见 memory：avalonia-reflection-indexer-binding）。
/// 只读展示，不接编辑（覆写 <see cref="DataGridColumn.GenerateEditingElement"/> 非必要——表格只读）。
/// </summary>
public sealed class CsvColumn : DataGridTextColumn
{
    public int ColumnIndex { get; init; }

    protected override Control GenerateElement(DataGridCell cell, object? dataItem)
    {
        var text = dataItem is IReadOnlyList<string> row && ColumnIndex >= 0 && ColumnIndex < row.Count
            ? row[ColumnIndex] ?? string.Empty
            : string.Empty;
        var textBlock = new TextBlock
        {
            Text = text,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(6, 2),
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        return textBlock;
    }
}

/// <summary>
/// CSV 产物体文件的表格视图（labs 知识对象页上栏，<see cref="Acme.Desktop.Mvvm.Knowledge.CsvTableViewModel"/> 驱动）。
/// 顶部一条「详情表 / 透视表」切换 + 透视列/值列 ComboBox（仅透视视图显示）+ 下方 ProDataGrid 表格。
/// ProDataGrid（MIT，Avalonia 作者出品）负责虚拟化/滚动，扛 2 万+ 行大表；单元格文本经 <see cref="CsvColumn"/>
/// 零绑定直接赋值，不依赖反射绑定。
/// </summary>
public partial class CsvTableView : UserControl
{
    private CsvTableViewModel? _vm;
    private bool _rebuilding; // 重建列期间守卫，防 ItemsSource/Columns 赋值触发重入

    public CsvTableView()
    {
        InitializeComponent();
        DataGrid.LoadingRow += (_, _) => { }; // 保留事件（未来如需行级定制）；文本已在 GenerateElement 赋值
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CsvTableViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        Rebuild();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Headers/Rows 变化（详情/透视切换、透视列/值列切换）→ 重建列 + 行
        if (e.PropertyName is nameof(CsvTableViewModel.Headers) or nameof(CsvTableViewModel.Rows))
            Rebuild();
        else if (e.PropertyName == nameof(CsvTableViewModel.IsPivot))
            Rebuild();
    }

    /// <summary>按当前 Headers/Rows 重建 ProDataGrid 列与行。</summary>
    private void Rebuild()
    {
        if (_vm is null) return;
        _rebuilding = true;
        try
        {
            var headers = _vm.Headers;
            var rows = _vm.Rows;

            // 动态建列：每列一个 CsvColumn，Header=表头名，ColumnIndex 用于 GenerateElement 取对应单元格。
            DataGrid.Columns.Clear();
            for (var i = 0; i < headers.Count; i++)
            {
                DataGrid.Columns.Add(new CsvColumn
                {
                    Header = headers[i],
                    ColumnIndex = i,
                    IsReadOnly = true,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                });
            }
            // 行数据：IReadOnlyList<string> 每行；GenerateElement 按 ColumnIndex 取值。
            DataGrid.ItemsSource = rows;
        }
        finally
        {
            _rebuilding = false;
        }
    }
}
