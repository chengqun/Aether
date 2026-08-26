using System;
using System.IO;
using System.Threading.Tasks;
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
            // （.md→markdown 预览；.md→知识对象表单；无 README 则不动编辑器）。
            await _vm.OpenFolderReadmeAsync(node.Path);
            return;
        }
        // 已开则激活、不重复读盘
        if (_vm.ActivateIfOpen(node.Path)) return;
        await _vm.OpenFromDiskAsync(node.Path);
    }

    // —— 右键菜单（对标 VSCode 资源管理器常用文件操作）——
    // 右键时 TreeView 已选中该节点 → 节点经 VM.SelectedNode 取（与 NotesTreeView 同范式），
    // 不依赖菜单项 DataContext（ContextMenu 是独立 popup，DataContext 继承 TreeView=VM 而非 TreeViewItem）。
    // 共用动作（对话框/剪贴板/在文件管理器中显示/废纸篓删除）在 <see cref="FileTreeOps"/>。

    private async void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { IsFolder: true } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var name = await FileTreeOps.PromptNameAsync(parent, "新建文件", $"在「{node.Name}」中新建文件", "文件名");
        if (string.IsNullOrWhiteSpace(name)) return;
        var target = Path.Combine(node.Path, name.Trim());
        if (File.Exists(target) || Directory.Exists(target))
        {
            _vm.StatusMessage = $"已存在同名文件：{name.Trim()}";
            return;
        }
        try
        {
            await File.WriteAllTextAsync(target, string.Empty);
            await _vm.OpenFromDiskAsync(target);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"新建文件失败：{ex.Message}";
        }
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { IsFolder: true } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var name = await FileTreeOps.PromptNameAsync(parent, "新建文件夹", $"在「{node.Name}」中新建文件夹", "文件夹名");
        if (string.IsNullOrWhiteSpace(name)) return;
        var target = Path.Combine(node.Path, name.Trim());
        if (File.Exists(target) || Directory.Exists(target))
        {
            _vm.StatusMessage = $"已存在同名文件夹：{name.Trim()}";
            return;
        }
        try { Directory.CreateDirectory(target); }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"新建文件夹失败：{ex.Message}";
        }
    }

    // —— 打开文件（文件节点）——

    private async void OnOpenNodeClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { IsFolder: false } node) return;
        if (_vm.ActivateIfOpen(node.Path)) return;
        await _vm.OpenFromDiskAsync(node.Path);
    }

    // —— 重命名（文件/文件夹通用。输入框弹窗；成功后 watcher 自动刷树）——

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var name = await FileTreeOps.PromptNameAsync(parent, "重命名", $"重命名「{node.Name}」", "新名称", node.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var newName = name.Trim();
        if (string.Equals(newName, node.Name, StringComparison.Ordinal)) return; // 未改
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _vm.StatusMessage = "名称包含非法字符";
            return;
        }
        var target = Path.Combine(Path.GetDirectoryName(node.Path)!, newName);
        if (string.Equals(target, node.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target) || Directory.Exists(target))
        {
            _vm.StatusMessage = $"已存在同名项：{newName}";
            return;
        }
        try
        {
            // 文件夹→移动目录；文件→File.Move（保留 opened tab 在其下的引用，VM 重载会刷新）
            if (node.IsFolder) Directory.Move(node.Path, target);
            else File.Move(node.Path, target);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"重命名失败:{ex.Message}";
        }
    }

    // —— 删除（文件/文件夹通用。经确认对话框；macOS 移入废纸篓可恢复，其它平台永久删除。watcher 自动刷树）——

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var disposal = node.IsFolder ? "将删除该文件夹及其全部内容。" : "文件将被删除。";
        if (OperatingSystem.IsMacOS())
            disposal = node.IsFolder ? "该文件夹及其内容将移入废纸篓。" : "文件将移入废纸篓。";
        var ok = await FileTreeOps.ConfirmAsync(parent, "删除", $"确定删除「{node.Name}」？\n{disposal}", "删除");
        if (!ok) return;
        try { FileTreeOps.Delete(node); }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    // —— 复制路径（绝对）/ 复制相对路径（相对项目根）——

    private void OnCopyPathClick(object? sender, RoutedEventArgs e)
        => CopyToClipboard(relative: false);

    private void OnCopyRelativePathClick(object? sender, RoutedEventArgs e)
        => CopyToClipboard(relative: true);

    private async void CopyToClipboard(bool relative)
    {
        if (_vm?.SelectedNode is not { } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var text = node.Path;
        if (relative && !string.IsNullOrEmpty(_vm.RootPath))
        {
            var root = _vm.RootPath!;
            if (text.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                text = text.Length > root.Length
                    ? text.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : string.Empty;
        }
        await FileTreeOps.CopyToClipboardAsync(parent, text);
    }

    // —— 在文件管理器中显示 ——

    private void OnRevealInFileManagerClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        FileTreeOps.RevealInFileManager(node);
    }
}
