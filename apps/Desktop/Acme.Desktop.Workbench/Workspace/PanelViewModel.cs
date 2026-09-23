using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>已解析 panel 实例（携带 ViewModel）。</summary>
public sealed class PanelViewModel : IPanel
{
    public PanelViewModel(string regionId, string key, string title, string icon, object? viewModel, string? group = null)
    {
        RegionId = regionId;
        Key = key;
        Title = title;
        Icon = icon;
        ViewModel = viewModel;
        Group = group;
    }

    public string RegionId { get; }
    public string Key { get; }
    public string Title { get; }
    public string Icon { get; }
    public string? Group { get; }
    public object? ViewModel { get; }
}
