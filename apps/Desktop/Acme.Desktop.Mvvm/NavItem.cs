using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 导航项（数据驱动，租户在 MainViewModelBase 子类里 Populate，天然支持租户差异——
/// qifu 4 项，stockapp 多 CTP/回测）。不用 enum，因 enum 无法跨租户扩展。
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public ICommand Command { get; init; } = null!;

    /// <summary>是否为当前页（由 MainViewModelBase 在 CurrentPage 变化时同步）。按钮 IsEnabled="{Binding !IsCurrent}"。</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
