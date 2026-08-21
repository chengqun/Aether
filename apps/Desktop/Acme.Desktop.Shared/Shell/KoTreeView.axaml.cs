using System;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 知识对象页 <c>.ok</c> 树视图（shell 级独立左栏，镜像 <see cref="FileTreeView"/>）。
/// 占满高度——宽度由外层 <c>LayoutSplit</c> 控制（终端只压右栏编辑器区，不压本树）。
/// 仅含树头「知识对象 ▾」（打开知识库目录/关闭知识库）+ <see cref="TreeView"/> + 未开知识库空态。
/// 与 <see cref="KnowledgeObjectsView"/>（右栏表单/源码）共享同一 <see cref="KnowledgeObjectsViewModel"/> 单例——
/// 树点 <c>.ok</c> 文件/目录 → VM.<c>SelectedNode</c> 双绑变 → VM.<c>OnSelectedNodeChanged</c> 自动
/// <c>OpenKnowledgeAsync</c>（文件）/ <c>OpenFolderReadmeAsync</c>（目录→该目录 <c>readme.ok</c>）→ 右栏表单/源码刷新
///（README 亦在 VM 处理，无需 view 侧 SelectionChanged，区别 FileTreeView 在 view 侧分发）。
/// </summary>
public partial class KoTreeView : UserControl
{
    private KnowledgeObjectsViewModel? _vm;

    public KoTreeView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as KnowledgeObjectsViewModel;
    }

    // —— 打开知识库目录（文件夹选择器 → VM 扫描 .ok 树）——

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开知识库目录",
            AllowMultiple = false
        });
        if (folders is null || folders.Count == 0) return;
        await _vm.OpenFolderAsync(folders[0].Path.LocalPath);
    }
}
