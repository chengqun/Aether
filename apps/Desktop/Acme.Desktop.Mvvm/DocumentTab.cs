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

    /// <summary>是否 markdown 文件（驱动预览切换按钮可见性）。</summary>
    public bool IsMarkdown => string.Equals(SyntaxKey, "markdown", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string _content;

    /// <summary>未保存修改标记（标签显示圆点）。</summary>
    [ObservableProperty] private bool _isDirty;

    public DocumentTab(string filePath, string content, string? syntaxKey)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        SyntaxKey = syntaxKey;
        _content = content; // 字段直赋值不触发 OnContentChanged → IsDirty 保持 false
    }

    partial void OnContentChanged(string value) => IsDirty = true;
}
