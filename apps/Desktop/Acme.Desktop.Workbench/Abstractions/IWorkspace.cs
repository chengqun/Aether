using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>活动工作台实例。ContentControl 绑定它（经 DataTemplate → WorkspaceView 渲染其布局树）。</summary>
public interface IWorkspace
{
    string Id { get; }
    string Title { get; }
    IReadOnlyList<IPanel> Panels { get; }
    LayoutNode Layout { get; }

    /// <summary>当前选中 tab 索引（MVP：单 Tabbed 组）。WorkspaceView 双向同步 TabControl.SelectedIndex。</summary>
    int SelectedTabIndex { get; set; }

    Task InitializeAsync(CancellationToken ct = default);
    Task DeactivateAsync(CancellationToken ct = default);
}
