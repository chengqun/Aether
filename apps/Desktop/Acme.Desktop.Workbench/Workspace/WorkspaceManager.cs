using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Workbench.Abstractions;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>IWorkspaceManager 默认实现：按定义构 panel（VM 经 ViewRegistry 解析）→ 建 WorkspaceViewModel。</summary>
public sealed class WorkspaceManager : IWorkspaceManager
{
    private readonly IViewRegistry _viewRegistry;

    public WorkspaceManager(IViewRegistry viewRegistry)
    {
        _viewRegistry = viewRegistry;
    }

    public IWorkspace? Active { get; private set; }

    public event EventHandler<IWorkspace>? WorkspaceActivated;

    public async Task ActivateAsync(WorkspaceDefinition definition, CancellationToken ct = default)
    {
        if (Active is not null)
            await Active.DeactivateAsync(ct);

        var panels = definition.Panels
            .Select(d => (IPanel)new PanelViewModel(
                d.RegionId, d.Key, d.Title, d.Icon,
                _viewRegistry.CreateViewModel(d.Key)))
            .ToList();

        var selectedIndex = LayoutNode.FindFirstTabbed(definition.Layout)?.SelectedIndex ?? 0;
        var ws = new WorkspaceViewModel(definition.Id, definition.Title, panels, definition.Layout, selectedIndex);
        await ws.InitializeAsync(ct);

        Active = ws;
        WorkspaceActivated?.Invoke(this, ws);
    }
}
