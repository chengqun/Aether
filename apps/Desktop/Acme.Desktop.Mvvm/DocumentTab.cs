using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 打开文档标签：一个磁盘文件的可编辑视图。承载文件路径/名 + 语法 key + 内容 + 脏标记。
/// 语法 key 由 View 侧 <c>CodeEditorSetup.SyntaxForExtension</c> 按扩展名算出后传入——VM 不引 Avalonia。
/// </summary>
public partial class DocumentTab : ObservableObject
{
    /// <summary>磁盘绝对路径（保存写回此处）。</summary>
    public string FilePath { get; }

    /// <summary>文件名（标签标题）。</summary>
    public string FileName { get; }

    /// <summary>语法 key（csharp/python/json/yaml…），null=纯文本。驱动 AvaloniaEdit 高亮。</summary>
    public string? SyntaxKey { get; }

    /// <summary>是否 markdown 文件（驱动预览切换按钮可见性）。.md 的 SyntaxKey 也映射到 markdown，
    /// 故 .md 在文档页与 .md 一样走上下两栏 markdown 预览/源码（知识对象有专门的「知识对象」nav 页打开表单）。</summary>
    public bool IsMarkdown => string.Equals(SyntaxKey, "markdown", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _content;

    /// <summary>未保存修改标记（标签显示圆点）。</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>磁盘文件已变更而本标签有未存改动（外部写盘冲突）——标签显 ⚠ + 重载按钮，不静默覆盖用户改动。</summary>
    [ObservableProperty] private bool _isStale;

    /// <summary>上次载入/保存时记录的磁盘最后写入时间（<see cref="FileSystemWatcher"/> 重载判定基准）。</summary>
    public DateTime LastDiskWrite { get; set; }

    public DocumentTab(string filePath, string content, string? syntaxKey)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        SyntaxKey = syntaxKey;
        _content = content; // 字段直赋值不触发 OnContentChanged → IsDirty 保持 false
        try { LastDiskWrite = File.GetLastWriteTime(filePath); }
        catch { LastDiskWrite = DateTime.MinValue; }
    }

    partial void OnContentChanged(string value) => IsDirty = true;

    /// <summary>外部（agent/外部编辑器）写盘后重载内容，不置 <see cref="IsDirty"/>。
    /// 直接赋 _content 字段 + 手发 PropertyChanged，绕过生成的 setter（其会调 OnContentChanged→IsDirty=true）。
    /// 仅用于 watcher 触发的非 dirty 标签静默刷新；dirty 标签走 <see cref="IsStale"/> 提示由用户决定。</summary>
    public void SetContentSilently(string content)
    {
#pragma warning disable MVVMTK0034 // 故意直赋 _content 字段绕过生成的 setter（其调 OnContentChanged→IsDirty=true）；外部重载不应置 dirty。
        _content = content;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Content));
    }
}
