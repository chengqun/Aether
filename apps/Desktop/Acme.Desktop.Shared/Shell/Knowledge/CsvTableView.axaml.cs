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

/// <summary>字段胶囊：一个可拖拽的字段标签。数据源（<see cref="CsvFieldRef"/>）经 DataContext 绑定；
/// 按下左键启动 Avalonia 拖拽携带该字段。拖拽源=字段列表胶囊 + 已放置槽位胶囊（可拖回列表或拖到其它区）。</summary>
public sealed class CsvFieldChip : Border
{
    private static readonly DataFormat<CsvFieldRef> Format = DataFormat.CreateInProcessFormat<CsvFieldRef>("csv-field");

    public CsvFieldChip()
    {
        Classes.Add("csv-chip");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CsvFieldRef field)
        {
            Classes.Set("chip-pivot", field.Kind == CsvFieldKind.Pivot);
            Classes.Set("chip-measure", field.Kind == CsvFieldKind.Measure);
            Classes.Set("chip-dimension", field.Kind == CsvFieldKind.Dimension);
            Child = new TextBlock { Text = field.Name, FontSize = 12 };
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is not CsvFieldRef field || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var item = DataTransferItem.Create(Format, field);
        // macOS 原生拖拽（NSCoreDragManager）要求 pasteboard 里至少有系统可序列化格式（文本/文件），
        // 纯进程内 InProcessFormat 的 .NET 对象引用无法进系统 pasteboard → beginDraggingSession 抛 NSException。
        // 附带 SetText 使拖拽能在 macOS 正常启动；drop 时仍用 TryGetRaw(InProcessFormat) 取回 .NET 对象。
        item.SetText(field.Name);
        var transfer = new DataTransfer();
        transfer.Add(item);
        _ = DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
    }
}

/// <summary>从拖拽事件取字段引用（进程内 DataFormat）。无则 null。</summary>
internal static class CsvDragData
{
    private static readonly DataFormat<CsvFieldRef> Format = DataFormat.CreateInProcessFormat<CsvFieldRef>("csv-field");

    internal static CsvFieldRef? GetField(IDataTransfer transfer)
    {
        foreach (var item in transfer.Items)
        {
            if (item.Formats.Contains(Format) && item.TryGetRaw(Format) is CsvFieldRef field)
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

        // 四个投放区挂 drop 事件 + 字段列表（拖回移除）。
        WireDropZone(RowDropZone);
        WireDropZone(ColumnDropZone);
        WireDropZone(NumeratorDropZone);
        WireDropZone(DenominatorDropZone);
        WireDropZone(FieldsDropZone);
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

    /// <summary>给一个投放区挂 drag-over/drop/leave 事件。</summary>
    private void WireDropZone(Border zone)
    {
        DragDrop.SetAllowDrop(zone, true);
        DragDrop.AddDragOverHandler(zone, (_, e) =>
        {
            var ok = CsvDragData.GetField(e.DataTransfer) is not null;
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            zone.Classes.Set("drop-over", ok);
        });
        DragDrop.AddDragLeaveHandler(zone, (_, _) => { zone.Classes.Set("drop-over", false); });
        DragDrop.AddDropHandler(zone, (_, e) =>
        {
            zone.Classes.Set("drop-over", false);
            if (CsvDragData.GetField(e.DataTransfer) is CsvFieldRef field)
                OnFieldDropped(zone, field);
        });
    }

    private void OnFieldDropped(Border zone, CsvFieldRef field)
    {
        if (_vm is null) return;
        if (ReferenceEquals(zone, RowDropZone)) _vm.AssignToRowDimension(field);
        else if (ReferenceEquals(zone, ColumnDropZone)) _vm.AssignToColumn(field);
        else if (ReferenceEquals(zone, NumeratorDropZone)) _vm.AssignToNumerator(field);
        else if (ReferenceEquals(zone, DenominatorDropZone)) _vm.AssignToDenominator(field);
        else if (ReferenceEquals(zone, FieldsDropZone)) _vm.RemoveField(field);
    }
}
