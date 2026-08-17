using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>活动热力图 view（GitHub 式贡献图）。DataContext = <see cref="ActivityViewModel"/>。
/// 网格格子 PointerPressed 转发 SelectDayCommand（Border 无 Command，走 code-behind；其余均绑）。</summary>
public partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && c.DataContext is DayCell cell && DataContext is ActivityViewModel vm)
        {
            vm.SelectDayCommand.Execute(cell);
            e.Handled = true;
        }
    }
}
