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

        // 笔记编辑器：共享 NotesEditorView（从原 NotesView col2 拆出——标题栏 + 上下分栏 + 6 种 form 节点视图）。
        // 笔记树在 LeftTreePanelView（notes tab 时切显），两者共享同一 NotesViewModel 单例。
        Register(PanelKeys.Notes,
            () => new NotesEditorView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Notes);

        // 打开文档编辑器：共享 DocumentsEditorView（多标签 AvaloniaEdit 编辑器，从原 DocumentsView col2 拆出）。
        Register(PanelKeys.Documents,
            () => new DocumentsEditorView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Documents);

        // shell 级左栏切换层：LeftTreePanelView 叠放文件树 + 笔记树，按 tab 切显隐（documents→FileTreeView / notes→NotesTreeView）。
        // DataContext = MainViewModelBase 本身（子 view 各绑 Documents/Notes 共享 VM 单例）。
        Register(PanelKeys.LeftTree,
            () => new LeftTreePanelView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>());

        // 源代码管理：共享 SourceControlView（git 变更列表 + 左右并排 diff）。
        Register(PanelKeys.SourceControl,
            () => new SourceControlView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().SourceControl);

        // 集成终端：共享 TerminalView（多标签真实 PTY；agent run_in_terminal 工具经 ITerminalService）。
        Register(PanelKeys.Terminal,
            () => new TerminalView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Terminal);

        // 专家：共享 ExpertsView（6 tab）。
        Register(PanelKeys.Experts,
            () => new ExpertsView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Experts);

        // 自动化：共享 AutomationView（所有租户都有此页）。
        Register(PanelKeys.Automation,
            () => new AutomationView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Automation);

        // 租户专属 panel（Stockapp 期货/回测/股票页等）。无注册时 contributors 为空 enumerable。
        foreach (var c in contributors)
            c.Contribute(this, serviceProvider);
    }
}
