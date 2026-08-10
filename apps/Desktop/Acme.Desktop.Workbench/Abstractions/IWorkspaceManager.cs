using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>工作区管理器：拥有活动工作台 + 生命周期。宿主绑定 <c>ActiveWorkspace</c>。</summary>
public interface IWorkspaceManager
{
    IWorkspace? Active { get; }

    /// <summary>按定义激活工作台（构 panel + 建 <see cref="IWorkspace"/>）。</summary>
    Task ActivateAsync(WorkspaceDefinition definition, CancellationToken ct = default);

    event EventHandler<IWorkspace>? WorkspaceActivated;
}
