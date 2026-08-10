using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 主题选项（内置或用户文件）。两处 UI（设置「外观」tab + 账号菜单快捷切换）共用，
/// <see cref="IsCurrent"/> 经 INotifyPropertyChanged 实时反映选中态——host 改选中时刷新所有项，UI 自动重染。
/// </summary>
public sealed partial class ThemeOption : ObservableObject
{
    /// <summary>唯一标识：内置 "light"/"dark"；用户主题=去扩展名文件名。</summary>
    public string Name { get; }

    /// <summary>显示描述（内置给中文说明；用户主题给文件名）。</summary>
    public string? Description { get; }

    /// <summary>是否内置（light/dark）。</summary>
    public bool IsBuiltIn { get; }

    /// <summary>是否当前选中（高亮/禁用再选）。</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public ThemeOption(string name, string? description, bool isBuiltIn, bool isCurrent = false)
    {
        Name = name;
        Description = description;
        IsBuiltIn = isBuiltIn;
        IsCurrent = isCurrent;
    }
}
