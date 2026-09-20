using System;
using System.IO;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 知识对象引用改写助手（纯函数）。改名/迁移/合并后改写入边 targetId 用的**规范写法**：
/// 同目录互链 = 裸名 stem（目录内互链惯例——目录迁移不断链）；跨目录 = labs 根相对路径
/// 带 <c>knowledge/</c> 前缀、不含扩展名（库内文档的根相对权威写法）。两者都是
/// <see cref="OkTargetResolver.CandidateOkPaths"/> 的一级候选，改写后的链接确定可解析。
/// </summary>
public static class KnowledgeLinkRewriter
{
    /// <summary>算改写后的规范 targetId。<paramref name="sourceFileFullPath"/> = 持有该 link 的文件全路径；
    /// <paramref name="targetFileFullPath"/> = 被引用对象的新全路径；<paramref name="labsRoot"/> = labs 根。</summary>
    public static string CanonicalTargetId(string sourceFileFullPath, string targetFileFullPath, string labsRoot)
    {
        var stem = OkTargetResolver.StemOf(targetFileFullPath);
        try
        {
            var sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourceFileFullPath));
            var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetFileFullPath));
            if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
                return stem; // 同目录：裸名（目录内互链惯例，目录整体迁移不断链）

            var rel = Path.GetRelativePath(Path.GetFullPath(labsRoot), Path.GetFullPath(targetFileFullPath));
            // 去扩展名（targetId 约定不含 .md；解析侧自动补）
            if (rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && rel.Length > 3)
                rel = rel[..^3];
            return rel;
        }
        catch
        {
            return stem; // 路径计算异常兜底：裸名（经全局兜底仍可解析）
        }
    }
}
