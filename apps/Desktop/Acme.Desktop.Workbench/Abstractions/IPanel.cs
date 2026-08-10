namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>已解析的 panel 实例：携带其 ViewModel（由 <see cref="IViewRegistry"/> 按 Key 解析）。</summary>
public interface IPanel
{
    string RegionId { get; }
    string Key { get; }
    string Title { get; }
    string Icon { get; }
    object? ViewModel { get; }
}
