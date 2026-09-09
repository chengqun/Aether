using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Knowledge;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 值字段设置弹窗：勾选「显示为百分比」+ 选汇总方式（求和/计数/平均/最大/最小，仅普通字段）+ 设小数位数。
/// 确定后写回 <see cref="CsvValueSpec"/>（AsPercent/Aggregation/Decimals），由 VM 重建。
/// </summary>
public partial class ValueFieldDialog : ThemedWindow
{
    private readonly CsvValueSpec _spec;

    public ValueFieldDialog(CsvValueSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        TitleText.Text = $"值字段：{spec.Name}";
        PercentCheck.IsChecked = spec.AsPercent;

        // 汇总方式：仅普通字段可切换；公式字段隐藏（固定「聚合后求值」）。
        if (spec.IsFormula)
        {
            AggregationRow.IsVisible = false;
        }
        else
        {
            AggregationBox.SelectedIndex = (int)spec.Aggregation;
        }

        // 小数位数：null 显示空。
        DecimalsBox.Text = spec.Decimals?.ToString() ?? string.Empty;

        if (spec.IsFormula)
        {
            FormulaText.Text = $"公式：{spec.Formula}";
            FormulaText.IsVisible = true;
        }
    }

    public Task ShowDialogAsync(Window owner) => ShowDialog(owner);

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _spec.AsPercent = PercentCheck.IsChecked == true;
        if (!_spec.IsFormula && AggregationBox.SelectedIndex >= 0)
            _spec.Aggregation = (CsvAggregation)AggregationBox.SelectedIndex;

        var decText = DecimalsBox.Text?.Trim();
        _spec.Decimals = int.TryParse(decText, out var d) ? d : null;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
