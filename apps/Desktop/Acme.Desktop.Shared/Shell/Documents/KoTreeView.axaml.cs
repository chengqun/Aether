using System;
using System.IO;
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

    // —— 右键菜单（镜像 FileTreeView，对标 VSCode 资源管理器常用文件操作）——
    // .ok 树节点是 FileNode（IsFolder 区分文件/文件夹）。共用动作在 FileTreeOps。
    // 节点经 VM.SelectedNode 取（右键时已选中），不依赖菜单项 DataContext（ContextMenu 是 popup，DataContext=VM）。
    // 「新建知识对象」写带 front-matter 的 .ok 模板；「打开」复用 VM.OnSelectedNodeChanged 的 OpenKnowledgeAsync。

    /// <summary>新知识对象默认内容（与 NoteNodeTypeRegistry.KnowledgeTemplate 同构：kind+links front-matter + body）。</summary>
    private const string OkTemplate = "---\nkind: Entity\nlinks: []\n---\n（在此写知识说明 / 粘贴结构化数据 / 任何 markdown）\n";

    private async void OnNewFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { IsFolder: true } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var name = await FileTreeOps.PromptNameAsync(parent, "新建知识对象", $"在「{node.Name}」中新建知识对象", "文件名（不含 .md）");
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        var target = Path.Combine(node.Path, trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith(".ok", StringComparison.OrdinalIgnoreCase)
            ? trimmed : trimmed + ".md");
        if (File.Exists(target) || Directory.Exists(target))
        {
            _vm.StatusMessage = $"已存在同名项：{Path.GetFileName(target)}";
            return;
        }
        try
        {
            await File.WriteAllTextAsync(target, OkTemplate);
            // 直接打开新对象（watcher 去抖刷树会补齐树节点；此处不依赖刷树即开）
            await _vm.OpenKnowledgeByPathAsync(target);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"新建知识对象失败：{ex.Message}";
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

    private void OnOpenNodeClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        // 复用选中→打开语义：重设 SelectedNode 触发 VM.OnSelectedNodeChanged（.ok→OpenKnowledgeAsync；目录→README）
        _vm.SelectedNode = node;
    }

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        var parent = TopLevel.GetTopLevel(this) as Window;
        if (parent is null) return;
        var name = await FileTreeOps.PromptNameAsync(parent, "重命名", $"重命名「{node.Name}」", "新名称", node.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var newName = name.Trim();
        if (string.Equals(newName, node.Name, StringComparison.Ordinal)) return;
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
            if (node.IsFolder) Directory.Move(node.Path, target);
            else File.Move(node.Path, target);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"重命名失败：{ex.Message}";
        }
    }

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

    private void OnRevealInFileManagerClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedNode is not { } node) return;
        FileTreeOps.RevealInFileManager(node);
    }
}
