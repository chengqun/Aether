using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>shell 级左栏切换层：叠放 FileTreeView（documents tab）+ NotesTreeView（notes tab）+
/// SourceControlPanelView（sourcecontrol tab），按当前 tab 切显隐。
/// 由 WorkspaceView.ApplyLeftTreeVisibility 经 SetActiveTree 驱动（非 documents/notes/sourcecontrol tab 时整个左栏列宽 0 隐藏）。
/// DataContext = MainViewModelBase；子 view DataContext 绑 {Binding Documents}/{Binding Notes}/{Binding SourceControl} 共享 VM 单例。</summary>
public partial class LeftTreePanelView : UserControl
{
    public LeftTreePanelView()
    {
        InitializeComponent();
    }

    /// <summary>切换活动树：documents→文件树 / notes→笔记树 / sourcecontrol→SCM 面板 / null→都隐（左栏列宽 0 由 WorkspaceView 控制）。
    /// public 因跨程序集：LeftTreePanelView 在 Shared，WorkspaceView 在 Workbench。</summary>
    public void SetActiveTree(string? kind)
    {
        if (FileTreePanel is null || NotesTreePanel is null || ScmTreePanel is null) return;
        FileTreePanel.IsVisible = kind == "documents";
        NotesTreePanel.IsVisible = kind == "notes";
        ScmTreePanel.IsVisible = kind == "sourcecontrol";
    }
}
