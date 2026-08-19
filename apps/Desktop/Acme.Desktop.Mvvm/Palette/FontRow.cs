using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm.Palette;

/// <summary>主题编辑器一个字体 token 的可编辑项。
/// <see cref="Value"/> 是带 fallback 的 FontFamily 串（如 "Segoe UI, PingFang SC, sans-serif"），
/// 直接写主题 JSON 的 fonts 节点；TextBox 手输 + 旁配预览 TextBlock（FontFamily 绑此值看效果）。</summary>
public sealed partial class FontRow : ObservableObject
{
    /// <summary>显示标签（如「UI 字体」「等宽字体」）。</summary>
    public string Label { get; }

    /// <summary>主题 JSON fonts 节点的 key（UiFont/MonoFont/CodeFont）。</summary>
    public string Key { get; }

    [ObservableProperty]
    private string _value = "";

    public FontRow(string label, string key, string value = "")
    {
        Label = label;
        Key = key;
        _value = value;
    }
}
