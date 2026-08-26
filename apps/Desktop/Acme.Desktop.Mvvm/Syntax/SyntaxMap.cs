using System;

namespace Acme.Desktop.Mvvm.Syntax;

/// <summary>
/// 文件扩展名（含点，大小写不敏感）→ 语法 key 的单一来源。
/// 曾有两份分歧拷贝（<c>CodeEditorSetup.SyntaxForExtension</c> 在 Shared、<c>GitService.SyntaxForExtension</c> 在 Mvvm，
/// 因 Mvvm 不能引 Shared 而镜像）——本类收拢到 Mvvm（Shared 可引 Mvvm），两边都转发至此。
/// 未知扩展名返回 null（纯文本，不高亮）。返回的 key 由各 view 映射到 AvaloniaEdit 内置定义名。
/// </summary>
public static class SyntaxMap
{
    /// <summary>扩展名（含点）→ 语法 key。null/空 → null。</summary>
    public static string? SyntaxForExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".py" or ".pyw" => "python",
            ".md" or ".markdown" => "markdown",
            ".json" => "json",
            ".sql" => "sql",
            ".yaml" or ".yml" => "yaml",
            ".js" or ".mjs" or ".cjs" or ".ts" => "javascript",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".xml" or ".axaml" or ".xaml" or ".csproj" or ".props" or ".targets" => "xml",
            ".java" => "java",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".h" => "cpp",
            ".vb" => "vb",
            ".ps1" => "powershell",
            ".sh" => "shell",
            _ => null,
        };
    }
}
