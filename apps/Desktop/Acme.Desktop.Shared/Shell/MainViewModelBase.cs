using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 桌面 shell 的主 ViewModel 基类：导航栏状态 + 导航命令。租户子类在构造里 Populate <see cref="NavItems"/>。
/// 骨架阶段内容区为占位（CurrentPageLabel）；真实 View 随功能 phase 注入。
/// </summary>
public abstract partial class MainViewModelBase : ObservableObject
{
    [ObservableProperty]
    private string _currentPage = "";

    /// <summary>导航项集合（租户填充）。"新聊天" 通常为首项。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>右侧内容区占位标签（骨架阶段用；功能 phase 替换为真实 View）。</summary>
    public string CurrentPageLabel => string.IsNullOrEmpty(CurrentPage) ? "Aether 桌面" : $"页面：{CurrentPage}";

    /// <summary>切换到指定页面。导航按钮 Command 调此。</summary>
    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        CurrentPage = pageKey;
        OnPropertyChanged(nameof(CurrentPageLabel));
        foreach (var item in NavItems)
            item.IsCurrent = item.Key == pageKey;
    }

    /// <summary>子类调：注册一个导航项，Command 绑定到 NavigateTo(pageKey)。</summary>
    protected void AddNav(string key, string icon, string title)
    {
        NavItems.Add(new NavItem
        {
            Key = key,
            Icon = icon,
            Title = title,
            Command = NavigateToCommand,
        });
    }
}
