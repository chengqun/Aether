using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.Models;

public partial class ToolCategoryGroup : ObservableObject
{
    public string Category { get; }
    public ObservableCollection<ToolToggleItem> Tools { get; } = new();

    /// <summary>是否展开该类别的工具列表（默认折叠，避免一屏铺满）。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>展开/折叠指示符</summary>
    public string ExpandIndicator => IsExpanded ? "▼" : "▶";

    /// <summary>该类别下工具数量（标题展示用）</summary>
    public int ToolCount => Tools.Count;

    public ToolCategoryGroup(string category)
    {
        Category = category;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandIndicator));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}
