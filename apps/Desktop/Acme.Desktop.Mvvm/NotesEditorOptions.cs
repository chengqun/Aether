namespace Acme.Shell;

/// <summary>
/// Notes 编辑器运行时配置（由桌面组合根设置）。非 AI 选项，故不放 AetherAIOptions。
/// </summary>
public sealed class NotesEditorOptions
{
    /// <summary>粘贴图片的保存根目录（绝对路径）。markdown 引用用相对文件名，预览通过 AssetPathRoot 解析。</summary>
    public string? ImageRoot { get; set; }
}
