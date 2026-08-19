using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm.Palette;

/// <summary>主题编辑器一个数字 token（圆角/间距）的可编辑项。
/// <see cref="Value"/> 绑 NumericUpDown；值变触发 PaletteViewModel 重建 overlay 预览（圆角/间距实时变）。</summary>
public sealed partial class NumberRow : ObservableObject
{
    /// <summary>显示标签（如「圆角-小」「间距-中」）。</summary>
    public string Label { get; }

    /// <summary>主题 JSON radius/spacing 节点的 key（CornerRadiusSm/SpaceMd 等）。</summary>
    public string Key { get; }

    /// <summary>分类（圆角/间距）——驱动 UI 分区。</summary>
    public string Category { get; }

    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    [ObservableProperty]
    private double _value;

    public NumberRow(string label, string key, string category, double value, double min, double max, double step)
    {
        Label = label;
        Key = key;
        Category = category;
        _value = value;
        Min = min;
        Max = max;
        Step = step;
    }
}
