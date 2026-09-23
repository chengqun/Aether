using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 导航项（工作台侧栏数据驱动 nav：由 WorkbenchShellViewModel 从活动工作台的首个 LayoutTabbed 派生，
/// 天然支持租户差异——Qifu 7 项，Stockapp 多 期货/回测/股票）。不用 enum，因 enum 无法跨租户扩展。
/// </summary>
public sealed partial class NavItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public ICommand Command { get; init; } = null!;

    /// <summary>是否为分组首项（与前一项 Group 不同 → rail 里该项上方加间距分隔）。构建时由 RebuildNav 标记，不再变。</summary>
    public bool StartsGroup { get; init; }

    /// <summary>是否为当前页（由 WorkbenchShellViewModel.UpdateNavCurrent 在 SelectedTabIndex 变化时同步）。按钮 IsEnabled="{Binding !IsCurrent}"。</summary>
    [ObservableProperty]
    private bool _isCurrent;
}
