namespace Acme.Mobile.Mvvm.Models;

/// <summary>
/// 底部选择器 sheet（agent/LLM）列表项。UI 无关 plain class——列表每次打开重建，
/// 无需 INPC（IsSelected 在构建时定值，✓ 显示随重建刷新）。
/// </summary>
public sealed class PickerItem
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsSelected { get; set; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);
}
