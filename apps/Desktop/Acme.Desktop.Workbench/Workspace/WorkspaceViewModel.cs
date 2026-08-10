using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Workbench.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>IWorkspace 默认实现。ContentControl 绑它 → DataTemplate → WorkspaceView 渲染其布局树。</summary>
public partial class WorkspaceViewModel : ObservableObject, IWorkspace
{
    public WorkspaceViewModel(string id, string title, IReadOnlyList<IPanel> panels, LayoutNode layout, int selectedTabIndex)
    {
        Id = id;
        Title = title;
        Panels = panels;
        Layout = layout;
        _selectedTabIndex = selectedTabIndex;
    }

    public string Id { get; }
    public string Title { get; }
    public IReadOnlyList<IPanel> Panels { get; }
    public LayoutNode Layout { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
}
