using System;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm.Knowledge;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 值字段设置弹窗：勾选「显示为百分比」→ 写入 <see cref="CsvValueSpec.AsPercent"/> → 由 VM 重建。
/// 也显示该值字段的公式（计算字段）或列名（普通字段，只读）。
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
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
