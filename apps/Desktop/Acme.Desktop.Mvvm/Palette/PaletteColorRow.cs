using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm.Palette;

/// <summary>主题编辑器一个颜色 token 的可编辑项。
/// <see cref="Hex"/> 双向绑到 PaletteWindow 的 ColorView（经 HexToColorConverter）——用户拖色板即改此值，
/// 触发 <see cref="PaletteViewModel"/> 重建 overlay 预览。</summary>
public sealed partial class PaletteColorRow : ObservableObject
{
    /// <summary>显示标签（如「主色」「内容背景」）。</summary>
    public string Label { get; }

    /// <summary>主题 JSON 中的 token key（如 AccentColor/ContentBgBrush）。</summary>
    public string Key { get; }

    /// <summary>分组名（Accent/Sidebar/容器/文本/状态/气泡/工具卡）——驱动 ListBox GroupStyle 分组。</summary>
    public string Category { get; }

    /// <summary>是否主色入口——驱动派生族（hover/light/tint + accent 族 token）。改主色时联动重算派生项 Hex。</summary>
    public bool IsAccent { get; }

    /// <summary>是否派生族项——只读（由主色联动重算）。选中时右色板禁用 +「派生自主色」提示。</summary>
    public bool IsDerived { get; }

    /// <summary>是否可在右色板编辑（= 非派生）。选中派生项时绑 ColorView.IsEnabled=false。ctor-set，稳定。</summary>
    public bool IsEditable => !IsDerived;

    [ObservableProperty]
    private string _hex = "";

    public PaletteColorRow(string label, string key, string category, bool isAccent = false, bool isDerived = false)
    {
        Label = label;
        Key = key;
        Category = category;
        IsAccent = isAccent;
        IsDerived = isDerived;
    }
}
