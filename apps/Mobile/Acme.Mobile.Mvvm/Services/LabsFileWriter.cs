using System;
using System.IO;
using System.Threading.Tasks;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;

namespace Acme.Mobile.Mvvm.Services;

/// <summary>
/// labs 文件写盘 helper（UI 无关）：按 <see cref="CatalogNoteDirectory"/> 权威目录布局，把序列化后的
/// 源文本写到 <c>&lt;labsRoot&gt;/&lt;dir&gt;/&lt;name&gt;&lt;ext&gt;</c>。手机端「创建」各类型（Skill/Knowledge/
/// Rule/Workflow/MCP）共用——不依赖任何 catalog，直接落盘（labs 是权威存储，文件即真相源）。
/// 与桌面端 labs 文件树 &lt;see cref="KnowledgeObjectsViewModel"/&gt; 的写盘路径同构，两处所见一致。
/// </summary>
public static class LabsFileWriter
{
    /// <summary>把 <paramref name="source"/> 写到该类型的目录（文件名 = name + 目录权威扩展名）。</summary>
    /// <returns>写出的绝对路径。</returns>
    public static async Task<string> WriteAsync(string labsRoot, NoteNodeType type, string name, string source)
    {
        if (string.IsNullOrWhiteSpace(labsRoot)) throw new InvalidOperationException("labs 根目录未配置");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空", nameof(name));

        var layout = CatalogNoteDirectory.For(type);
        var dir = Path.Combine(labsRoot, layout.DirectoryName);
        Directory.CreateDirectory(dir);

        // Skill 特殊：文件夹节点 skills/<slug>/SKILL.md（name 是 slug）。其它类型平铺文件。
        var fullPath = type == NoteNodeType.Skill
            ? Path.Combine(dir, Sanitize(name), "SKILL.md")
            : Path.Combine(dir, Sanitize(name) + layout.Extension);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, source);
        return fullPath;
    }

    /// <summary>非法文件名字符替换为下划线（桌面端 <c>DirectoryAgentCatalog.Sanitize</c> 同语义）。</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var trimmed = new string(chars).Trim();
        return string.IsNullOrEmpty(trimmed) ? "untitled" : trimmed;
    }
}
