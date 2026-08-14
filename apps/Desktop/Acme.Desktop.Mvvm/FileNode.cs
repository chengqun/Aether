using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 文件树节点（「打开文档」页打开项目目录后的左侧树）。文件夹/文件二态，递归 <see cref="Children"/>。
/// 镜像 <see cref="NoteNodeVM"/> 的树绑定面：Icon/IconColor 是 Lucide path data / hex 色串（与 NoteNodeVM 同构），
/// 供 View 的 &lt;Path&gt; 渲染；IsExpanded 双绑驱动展开/收起。UI-agnostic（零 Avalonia 引用）。
/// </summary>
public partial class FileNode : ObservableObject
{
    /// <summary>显示名（文件/文件夹名）。</summary>
    public string Name { get; }

    /// <summary>磁盘绝对路径。</summary>
    public string Path { get; }

    /// <summary>是否文件夹（文件夹可展开，文件可点开编辑）。</summary>
    public bool IsFolder { get; }

    /// <summary>子节点（文件夹才有；文件为空集合）。TreeView TreeDataTemplate ItemsSource 绑此。</summary>
    public ObservableCollection<FileNode> Children { get; } = new();

    /// <summary>展开态（双绑 TreeViewItem.IsExpanded）。</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>图标 path data（Lucide：folder / file-text）。</summary>
    public string Icon => IsFolder
        ? FolderIcon
        : FileIcon;

    /// <summary>图标颜色（hex，明暗两主题均可见——与 NoteNodeVM/NoteIcons 同策略：小图标 stroke 色非大面积背景）。</summary>
    public string IconColor => IsFolder ? "#E0A800" : "#7A8290";

    /// <summary>图标是否填充（文件夹填充，文件描边）。</summary>
    public bool IconIsFill => IsFolder;

    public FileNode(string name, string path, bool isFolder)
    {
        Name = name;
        Path = path;
        IsFolder = isFolder;
    }

    public const string FolderIcon =
        "M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.93a2 2 0 0 1-1.66-.9l-.82-1.2A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13c0 1.1.9 2 2 2Z";

    public const string FileIcon =
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";
}
