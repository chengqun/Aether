using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 新建计算字段弹窗：输入「字段名」+「公式」（如 <c>[od_yi] / [obs_yi]</c>）。
/// 确定 → 返回 (名称, 公式)；取消 → 返回 (null, null)。字段名用 [列名] 包裹引用 CSV 列。
/// </summary>
public partial class CalculatedFieldDialog : ThemedWindow
{
    public string? ResultName { get; private set; }
    public string? ResultFormula { get; private set; }

    public CalculatedFieldDialog()
    {
        InitializeComponent();
    }

    /// <summary>模态显示，返回 (名称, 公式)；取消返回 (null, null)。</summary>
    public async Task<(string? Name, string? Formula)> ShowDialogAsync(Window owner)
    {
        ResultName = null;
        ResultFormula = null;
        await ShowDialog(owner);
        return (ResultName, ResultFormula);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        var formula = FormulaBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(formula)) return;
        ResultName = name;
        ResultFormula = formula;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        ResultName = null;
        ResultFormula = null;
        Close();
    }
}
