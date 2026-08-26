using System;
using System.Collections.Generic;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Acme.Desktop.Workbench.Abstractions;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Workbench.Workspace;

/// <summary>
/// 工作台视图注册表：把 view-key 绑到共享视图（ChatView/ExpertsView/AutomationView）+ VM 工厂。
/// VM 工厂惰性从 <see cref="MainViewModelBase"/> 单例取（Chat/Experts/Automation 都在基类上，覆盖所有租户派生 VM）。
/// 租户专属 panel（如 Stockapp 期货/回测/股票页）经 <see cref="IViewRegistryContributor"/> 注入——本库不依赖租户程序集。
/// </summary>
public sealed class WorkbenchViewRegistry : ViewRegistry
{
    public WorkbenchViewRegistry(IServiceProvider serviceProvider, IEnumerable<IViewRegistryContributor> contributors)
    {
        // 聊天右栏：共享 ChatView（紧凑），DataContext 绑 ChatViewModel。
        Register(PanelKeys.Chat,
            () => new ChatView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Chat);

        // 全屏 AI 聊天页（nav 首项，app 启动默认页）：ChatPageView 居中 ChatGPT 式输入，与右栏 ChatView 共享同一 ChatViewModel。
        Register(PanelKeys.ChatPage,
            () => new ChatPageView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Chat);

        // 打开文档编辑器：共享 DocumentsEditorView（多标签 AvaloniaEdit 编辑器，从原 DocumentsView col2 拆出）。
        Register(PanelKeys.Documents,
            () => new DocumentsEditorView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Documents);

        // shell 级左栏切换层：LeftTreePanelView 叠放文件树 + SCM 面板 + .md 树，按 tab 切显隐（documents→FileTreeView / sourcecontrol→SCM / knowledge→KoTreeView）。
        // DataContext = MainViewModelBase 本身（子 view 各绑 Documents/SourceControl/Knowledge 共享 VM 单例）。
        Register(PanelKeys.LeftTree,
            () => new LeftTreePanelView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>());

        // 源代码管理：共享 SourceControlView（git 变更列表 + 左右并排 diff）。
        Register(PanelKeys.SourceControl,
            () => new SourceControlView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().SourceControl);

        // 集成终端 + 浏览器网络：底部 BottomPanelView 复合视图（终端/网络 tab 内部切换，各自常驻不销毁；折叠复用终端 region toggle）。
        // agent run_in_terminal 工具经 ITerminalService（TerminalView 内）；browser_open/get_cookies 经 IBrowserService（BrowserView 内）。
        // DataContext = TerminalViewModel（vmFactory 注入）；BrowserView 的 BrowserViewModel 经 DesktopApp.Services 定位器解析（同 DocumentsEditorView 范式）。
        Register(PanelKeys.Terminal,
            () => new BottomPanelView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Terminal);

        // 活动热力图：共享 ActivityView（GitHub 式贡献图：Agent 执行 + Git 提交按日聚合，点格看当天明细）。
        Register(PanelKeys.Activity,
            () => new ActivityView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Activity);

        // 知识对象（文档版 .md 知识对象独立 nav 页）：左 .md 树 + 右上下两栏（表单/源码）。
        // 根目录=独立知识库目录（IKnowledgeDirectoryProvider，与文档页项目目录解耦，跨项目复用）。
        Register(PanelKeys.Knowledge,
            () => new KnowledgeObjectsView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Knowledge);

        // 知识对象源代码管理（独立 nav tab，与知识对象编辑页并列）：右 = 复用 SourceControlView（diff），
        // DataContext 绑 Knowledge.SourceControl（知识库目录的 git）；左栏变更列表由 LeftTreePanelView 的 knowledgescm 层提供。
        Register(PanelKeys.KnowledgeScm,
            () => new SourceControlView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Knowledge.SourceControl);

        // 浏览器网络（侧边栏 nav 全屏入口）：与底部 BottomPanelView 的「网络」tab 同源 BrowserViewModel 单例——两个 BrowserView 实例绑同一 VM，
        // 请求流双视图同步。底部 tab 适合边干别的边瞄一眼，侧边栏全屏适合细看请求流（agent browser_open/get_cookies 经同一 IBrowserService）。
        Register(PanelKeys.Browser,
            () => new BrowserView(),
            () => serviceProvider.GetRequiredService<MainViewModelBase>().Browser);

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
