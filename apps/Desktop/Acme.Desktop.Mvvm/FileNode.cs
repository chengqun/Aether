using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 文件树节点（「打开文档」页打开项目目录后的左侧树）。文件夹/文件二态，递归 <see cref="Children"/>。
/// 对齐 VSCode：文件夹不显文件类型图标（只靠 TreeViewItem 自带的展开/收起箭头）；
/// 文件按扩展名经 <see cref="FileIcons"/> 解析语言 logo（path data + 色 + fill/stroke）。
/// Icon/IconColor 是 path data / hex 色串（与 NoteNodeVM 同构），供 View 的 &lt;Path&gt; 渲染。
/// IsExpanded 双绑驱动展开/收起。UI-agnostic（零 Avalonia 引用）。
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

    // 图标在构造时按是否文件 + 文件名解析定（不变），存只读字段。文件夹走 Default 占位但不显（HasIcon=false）。
    private readonly string _icon;
    private readonly string _iconColor;
    private readonly bool _iconIsFill;

    /// <summary>图标 path data（文件=语言 logo / 文件夹=占位不显）。</summary>
    public string Icon => _icon;

    /// <summary>图标颜色（hex）。</summary>
    public string IconColor => _iconColor;

    /// <summary>图标是否填充。</summary>
    public bool IconIsFill => _iconIsFill;

    /// <summary>是否显示文件类型图标。VSCode 风：文件夹不显（只靠展开箭头），文件显。</summary>
    public bool HasIcon => !IsFolder;

    /// <summary>渲染填充版图标的 Path 应可见（文件且 fill 型）。文件夹/描边型为 false。</summary>
    public bool ShowFillIcon => HasIcon && _iconIsFill;

    /// <summary>渲染描边版图标的 Path 应可见（文件且 stroke 型）。文件夹/填充型为 false。</summary>
    public bool ShowStrokeIcon => HasIcon && !_iconIsFill;

    public FileNode(string name, string path, bool isFolder)
        : this(name, name, path, isFolder) { }

    /// <summary>同名重载：<paramref name="displayName"/> 显示名、<paramref name="iconFileName"/> 图标解析名。
    /// 知识对象页树显示名去后缀但图标仍按完整名解析知识对象图标时用；<paramref name="iconOverride"/> 显式指定图标
    ///（知识对象页 <c>.md</c> 文件统一知识对象图标，不随扩展名走 markdown logo）。</summary>
    public FileNode(string displayName, string iconFileName, string path, bool isFolder, FileIcons.Icon? iconOverride = null)
    {
        Name = displayName;
        Path = path;
        IsFolder = isFolder;
        // 文件夹不显图标，但仍给占位值（HasIcon=false 时 View 两个 Path 都不显）。
        var icon = iconOverride ?? (isFolder ? FileIcons.Default : FileIcons.ForFile(iconFileName));
        _icon = icon.Path;
        _iconColor = icon.Color;
        _iconIsFill = icon.IsFill;
    }

    /// <summary>通用文件图标 path（Lucide file-text，<see cref="FileIcons.Default"/> 用）。</summary>
    public const string FileIcon =
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";
}
