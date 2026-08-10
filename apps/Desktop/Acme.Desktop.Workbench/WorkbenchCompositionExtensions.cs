using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Acme.Desktop.Workbench.Theming;
using Acme.Desktop.Workbench.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench;

/// <summary>
/// Codex 工作台 DI 入口。注册工作台引擎（view registry / region manager / workspace manager）+ 默认工作台定义
/// + 主题应用器（让 Shared 层子窗口设置/Trace 经 <see cref="IThemeApplier"/> 叠加 overlay）。
/// 宿主在 <c>AddDesktopCommon</c> 之后调 <c>services.AddWorkbench()</c>。
/// </summary>
public static class WorkbenchCompositionExtensions
{
    /// <param name="defaultWorkspace">租户自定义默认工作台定义（null = 用 <see cref="DefaultWorkspace.Definition"/>）。
    /// Codex 传 null；Qifu/Stockapp 传各自的 def（含租户专属 panel）。</param>
    public static IServiceCollection AddWorkbench(this IServiceCollection services, WorkspaceDefinition? defaultWorkspace = null)
    {
        services.AddSingleton<IViewRegistry, WorkbenchViewRegistry>();
        services.AddSingleton<IRegionManager, RegionManager>();
        services.AddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.AddSingleton(defaultWorkspace ?? DefaultWorkspace.Definition);
        services.AddSingleton<IThemeApplier, ThemeApplier>();
        return services;
    }
}
