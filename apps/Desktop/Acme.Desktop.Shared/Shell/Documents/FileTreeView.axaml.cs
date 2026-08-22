using System;
using Acme.Desktop.Mvvm;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 文档页文件树视图（shell 级独立左栏，从原 <c>DocumentsView</c> 拆出）。
/// 占满高度——宽度由外层 <c>LayoutSplit</c> 控制（终端只压编辑器区，不压本树）。
/// 仅含树头「文件 ▾」3 项（打开项目目录/关闭项目/打开文件）+ <see cref="TreeView"/> + 未开项目空态。
/// 文件读 IO 在 VM <see cref="DocumentsViewModel.OpenFromDiskAsync"/>（拆分后独立树控件复用）；
/// 与 <see cref="DocumentsEditorView"/> 共享同一 <see cref="DocumentsViewModel"/> 单例——树点文件→VM.Open→Tabs/ActiveTab 变→编辑器响应。
/// </summary>
public partial class FileTreeView : UserControl
{
    private DocumentsViewModel? _vm;

    public FileTreeView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as DocumentsViewModel;
    }

    // —— 打开项目目录（文件夹选择器 → VM 扫描文件树）——

    private async void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开项目目录",
            AllowMultiple = false
        });
        if (folders is null || folders.Count == 0) return;
        await _vm.OpenFolderAsync(folders[0].Path.LocalPath);
    }

    // —— 打开单个文件（文件选择器，补充入口）——

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("代码与文档")
                {
                    Patterns = new[]
                    {
                        "*.cs","*.py","*.md","*.markdown","*.json","*.sql","*.yaml","*.yml",
                        "*.js","*.mjs","*.ts","*.html","*.htm","*.css","*.xml","*.axaml","*.xaml",
                        "*.csproj","*.java","*.cpp","*.cc","*.cxx","*.hpp","*.hh","*.h","*.vb","*.ps1","*.txt"
                    }
                },
                FilePickerFileTypes.All,
            },
        });
        if (files is null || files.Count == 0) return;
        foreach (var file in files)
            await _vm.OpenFromDiskAsync(file.Path.LocalPath);
    }

    // —— 树节点选中：文件→VM 读取并打开标签；文件夹→忽略 ——
    // SelectedItem 双绑已设 VM.SelectedNode；文件读 IO/二进制检测在 VM.OpenFromDiskAsync。

    private async void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not FileNode node) return;
        if (node.IsFolder)
        {
            // 点击目录：展开/收起仍由 TreeViewItem 默认处理；额外自动打开该目录 README
            // （.md→markdown 预览；.ok→知识对象表单；无 README 则不动编辑器）。
            await _vm.OpenFolderReadmeAsync(node.Path);
            return;
        }
        // 已开则激活、不重复读盘
        if (_vm.ActivateIfOpen(node.Path)) return;
        await _vm.OpenFromDiskAsync(node.Path);
    }
}
