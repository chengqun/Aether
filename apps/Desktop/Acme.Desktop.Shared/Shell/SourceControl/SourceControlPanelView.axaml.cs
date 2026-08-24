using Avalonia;
using Avalonia.Controls;

namespace Acme.Desktop.Shared.Shell;

/// <summary>SCM 左侧栏（从 <see cref="SourceControlView"/> 拆出的「变更/暂存/历史 + 提交框 + 远程同步工具栏」面板）。
/// 提升为 workspace 级 col0（<see cref="LeftTreePanelView"/> 叠放层之一），独占全高——使终端只在 diff 区下方，
/// 不再横跨变更面板（与文档/笔记页「左侧树独占全高 + 终端在主内容下方」布局一致）。
/// 逻辑全在 <see cref="SourceControlViewModel"/> 绑定；DataContext 由 <see cref="LeftTreePanelView"/> 设为
/// <c>{Binding SourceControl}</c>（与 diff 区 <see cref="SourceControlView"/> 共享同 VM 单例）。
/// <see cref="EmptyHint"/> 可覆盖「未打开目录」空态提示（项目 SCM 默认「在文档页打开一个 git 项目」；
/// 知识对象 SCM 经 <c>KoScmPanelView</c> 覆盖为「打开知识库目录后可用 Git」）。</summary>
public partial class SourceControlPanelView : UserControl
{
    /// <summary>「未打开目录」空态的第二行提示文本。默认指向文档页；知识对象 SCM 覆盖指向知识库目录。</summary>
    public static readonly StyledProperty<string> EmptyHintProperty =
        AvaloniaProperty.Register<SourceControlPanelView, string>(nameof(EmptyHint), defaultValue: "在「文档」页打开一个 git 项目");

    public string EmptyHint
    {
        get => GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    public SourceControlPanelView()
    {
        InitializeComponent();
    }
}
