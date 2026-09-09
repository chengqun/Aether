using System;
using System.Collections.Generic;
using System.ComponentModel;
using Acme.Desktop.Mvvm.Knowledge;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 自定义 CSV 列：override <see cref="DataGridTextColumn.GenerateElement"/> 直接从行数据
/// （<c>IReadOnlyList&lt;string&gt;</c>）按 <see cref="ColumnIndex"/> 取文本赋给 TextBlock——<b>零绑定、零反射</b>。
/// 绕开 Avalonia DataGrid 反射绑定单元格（<c>Cells[idx]</c>）在本仓不渲染的历史坑。
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

/// <summary>字段胶囊：一个可拖拽的字段标签。数据源经 DataContext 绑定（<see cref="CsvFieldItem"/> 或
/// <see cref="CsvFieldRef"/>）；按下左键启动 Avalonia 拖拽，携带统一的 <see cref="CsvFieldItem"/>。
/// 字段列表胶囊 DataContext 是 <see cref="CsvFieldItem"/>（可含计算字段）；行/列区胶囊是 <see cref="CsvFieldRef"/>。</summary>
public sealed class CsvFieldChip : Border
{
    private static readonly DataFormat<CsvFieldItem> Format = DataFormat.CreateInProcessFormat<CsvFieldItem>("csv-field");

    public CsvFieldChip()
    {
        Classes.Add("csv-chip");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var item = ToItem(DataContext);
        if (item is null) return;
        if (item.Field is { } f)
        {
            Classes.Set("chip-pivot", f.Kind == CsvFieldKind.Pivot);
            Classes.Set("chip-measure", f.Kind == CsvFieldKind.Measure);
            Classes.Set("chip-dimension", f.Kind == CsvFieldKind.Dimension);
            Classes.Set("chip-calculated", false);
        }
        else
        {
            Classes.Set("chip-calculated", true);
        }
        Child = new TextBlock { Text = item.Name, FontSize = 12 };
    }

    private static CsvFieldItem? ToItem(object? dc) => dc switch
    {
        CsvFieldItem i => i,
        CsvFieldRef f => CsvFieldItem.Plain(f),
        _ => null,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var item = ToItem(DataContext);
        if (item is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var transferItem = DataTransferItem.Create(Format, item);
        // macOS 原生拖拽要求 pasteboard 至少有系统可序列化格式 → 附带 SetText。
        transferItem.SetText(item.Name);
        var transfer = new DataTransfer();
        transfer.Add(transferItem);
        _ = DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
    }
}

/// <summary>从拖拽事件取字段条目（进程内 DataFormat）。无则 null。</summary>
internal static class CsvDragData
{
    private static readonly DataFormat<CsvFieldItem> Format = DataFormat.CreateInProcessFormat<CsvFieldItem>("csv-field");

    internal static CsvFieldItem? GetItem(IDataTransfer transfer)
    {
        foreach (var item in transfer.Items)
        {
            if (item.Formats.Contains(Format) && item.TryGetRaw(Format) is CsvFieldItem field)
                return field;
        }
        return null;
    }
}

/// <summary>
/// CSV 产物体文件的表格视图（labs 知识对象页上栏，<see cref="CsvTableViewModel"/> 驱动）。
/// 左侧字段列表（可拖拽胶囊）+ 三个投放区（行维度/列维度/值分子分母）+ 下方 ProDataGrid 表格。
/// 拖拽：字段胶囊 → 拖到投放区放下 → VM 更新槽位 → 重建透视。
/// ProDataGrid（MIT，Avalonia 作者出品）负责虚拟化/滚动；单元格文本经 <see cref="CsvColumn"/> 零绑定赋值。
/// </summary>
public partial class CsvTableView : UserControl
{
    private CsvTableViewModel? _vm;

    public CsvTableView()
    {
        InitializeComponent();
        // 详情/透视切换
        DetailButton.Click += (_, _) => _vm?.SwitchToDetailCommand.Execute(null);
        PivotButton.Click += (_, _) => _vm?.SwitchToPivotCommand.Execute(null);
        // 新建计算字段
        AddCalcButton.Click += OnAddCalculatedField;

        // 投放区挂 drop 事件 + 字段列表（拖回移除）。
        WireDropZone(RowDropZone, acceptCalculated: false);
        WireDropZone(ColumnDropZone, acceptCalculated: false);
        WireDropZone(ValueDropZone, acceptCalculated: true);
        WireDropZone(FilterDropZone, acceptCalculated: false);
        WireDropZone(FieldsDropZone, acceptCalculated: false);
    }

    private async void OnAddCalculatedField(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var win = new CalculatedFieldDialog();
        var (name, formula) = await win.ShowDialogAsync(owner);
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(formula))
            _vm.AddCalculatedField(name, formula);
    }

    private void OnValueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CsvValueSpec spec })
            _vm?.RemoveValueField(spec);
    }

    private async void OnValueSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button { Tag: CsvValueSpec spec }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var win = new ValueFieldDialog(spec);
        await win.ShowDialogAsync(owner);
        _vm.ValueFieldSettingsChanged(spec);
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => _vm?.Refresh();

    private void OnFilterRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CsvFilterField filter })
            _vm?.RemoveFilter(filter.Field);
    }

    private async void OnFilterValuesClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (sender is not Button { Tag: CsvFilterField filter }) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var win = new FilterValuesDialog(filter);
        await win.ShowDialogAsync(owner);
        _vm.ToggleFilterValue(filter);
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
        if (e.PropertyName is nameof(CsvTableViewModel.Headers) or nameof(CsvTableViewModel.Rows))
            Rebuild();
    }

    /// <summary>按当前 Headers/Rows 重建 ProDataGrid 列与行。</summary>
    private void Rebuild()
    {
        if (_vm is null) return;
        var headers = _vm.Headers;
        var rows = _vm.Rows;

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
        DataGrid.ItemsSource = rows;
    }

    // —— 拖拽投放区 ——

    /// <summary>给一个投放区挂 drag-over/drop/leave 事件。<paramref name="acceptCalculated"/> 为 true 才接受计算字段（仅值区）。</summary>
    private void WireDropZone(Border zone, bool acceptCalculated)
    {
        DragDrop.SetAllowDrop(zone, true);
        DragDrop.AddDragOverHandler(zone, (_, e) =>
        {
            var item = CsvDragData.GetItem(e.DataTransfer);
            // 计算字段只进值区（acceptCalculated）；普通字段全部区接受。
            var ok = item is not null && (acceptCalculated || !item.IsCalculated);
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            zone.Classes.Set("drop-over", ok);
        });
        DragDrop.AddDragLeaveHandler(zone, (_, _) => { zone.Classes.Set("drop-over", false); });
        DragDrop.AddDropHandler(zone, (_, e) =>
        {
            zone.Classes.Set("drop-over", false);
            if (CsvDragData.GetItem(e.DataTransfer) is { } item)
                OnFieldDropped(zone, item);
        });
    }

    private void OnFieldDropped(Border zone, CsvFieldItem item)
    {
        if (_vm is null) return;
        // 计算字段：只进值区。
        if (item.IsCalculated)
        {
            if (ReferenceEquals(zone, ValueDropZone)) _vm.AssignCalculatedToValue(item);
            return;
        }
        var field = item.Field;
        if (field is null) return;
        if (ReferenceEquals(zone, RowDropZone)) _vm.AssignToRowDimension(field);
        else if (ReferenceEquals(zone, ColumnDropZone)) _vm.AssignToColumn(field);
        else if (ReferenceEquals(zone, ValueDropZone)) _vm.AssignToValue(field);
        else if (ReferenceEquals(zone, FilterDropZone)) _vm.AssignToFilter(field);
        else if (ReferenceEquals(zone, FieldsDropZone)) _vm.RemoveField(field);
    }
}
