namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>已解析的 panel 实例：携带其 ViewModel（由 <see cref="IViewRegistry"/> 按 Key 解析）。</summary>
public interface IPanel
{
    string RegionId { get; }
    string Key { get; }
    string Title { get; }
    string Icon { get; }
    /// <summary>可选分组名（仅 nav rail 呈现用；null = 不分组）。来自 <see cref="PanelDescriptor.Group"/>。</summary>
    string? Group { get; }
    object? ViewModel { get; }
}
