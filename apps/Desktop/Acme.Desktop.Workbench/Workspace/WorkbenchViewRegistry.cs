using System;
using System.Collections.Generic;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>
/// 工作台视图注册表：把 view-key 绑到共享视图（ChatView/NotesView/ExpertsView/AutomationView）+ VM 工厂。
/// VM 工厂惰性从 <see cref="MainViewModelBase"/> 单例取（Chat/Notes/Experts/Automation 都在基类上，覆盖所有租户派生 VM）。
/// 租户专属 panel（如 Stockapp 期货/回测/股票页）经 <see cref="IViewRegistryContributor"/> 注入——本库不依赖租户程序集。
/// </summary>
public sealed class WorkbenchViewRegistry : ViewRegistry
{
    public WorkbenchViewRegistry(IServiceProvider serviceProvider, IEnumerable<IViewRegistryContributor> contributors)
    {
        // 聊天：共享 ChatView，DataContext 绑 ChatWorkspaceViewModel（行为与 Stockapp 一致）。
        Register(PanelKeys.Chat,
            () => new ChatView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Chat);

        // 笔记：共享 NotesView（含 AvaloniaEdit + form 节点两栏）。
        Register(PanelKeys.Notes,
            () => new NotesView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Notes);

        // 专家：共享 ExpertsView（6 tab）。
        Register(PanelKeys.Experts,
            () => new ExpertsView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Experts);

        // 自动化：共享 AutomationView（所有租户都有此页）。
        Register(PanelKeys.Automation,
            () => new AutomationView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Automation);

        // 测试 panel（验证 IRegionManager 注入路径）。
        Register(PanelKeys.Test,
            () => new TextBlock
            {
                Text = "✅ 插件面板注入成功",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontSize = 14,
            },
            () => null);

        // 租户专属 panel（Stockapp 期货/回测/股票页等）。无注册时 contributors 为空 enumerable。
        foreach (var c in contributors)
            c.Contribute(this, serviceProvider);
    }
}
