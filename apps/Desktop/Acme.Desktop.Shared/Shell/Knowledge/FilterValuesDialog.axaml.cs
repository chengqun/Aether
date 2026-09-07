using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Knowledge;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 筛选字段的值勾选弹窗：列出该筛选字段的全部去重值（CheckBox 多选），确定后写回
/// <see cref="CsvFilterField"/> 的 <see cref="CsvFilterField.Allowed"/> 集合（由 VM 调
/// <see cref="CsvTableViewModel.ToggleFilterValue"/> 重算 + 重建）。
/// </summary>
public partial class FilterValuesDialog : ThemedWindow
{
    private readonly CsvFilterField _filter;

    public FilterValuesDialog(CsvFilterField filter)
    {
        _filter = filter;
        InitializeComponent();
        TitleText.Text = $"筛选：{filter.Field.Name}";
        ValuesList.ItemsSource = filter.ValueOptions;
    }

    /// <summary>模态显示。</summary>
    public Task ShowDialogAsync(Window owner) => ShowDialog(owner);

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        foreach (var o in _filter.ValueOptions) o.IsSelected = true;
    }

    private void OnSelectNone(object? sender, RoutedEventArgs e)
    {
        foreach (var o in _filter.ValueOptions) o.IsSelected = false;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
