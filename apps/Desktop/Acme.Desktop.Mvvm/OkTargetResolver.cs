using System;
using System.Collections.Generic;
using System.IO;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// <c>.ok</c> 文件目标解析共享助手（纯函数）。文档版知识对象的 link <c>targetId</c> 是项目内另一个
/// <c>.ok</c> 文件的相对路径（如 <c>Hive表/fin_dw.dwd_xxx</c>），此 helper 把路径解析到磁盘文件。
/// <see cref="DocumentsViewModel"/> 图解析（「查看图」闭包构建，<c>ResolveTarget</c> 取定义跟出边）与
/// <see cref="Acme.Desktop.Shared.Shell.DocumentKnowledgeTool"/> Read（<c>ResolveRaw</c> 取目标原文内联）共用，
/// 避免逻辑漂移。镜像原 DocumentsViewModel 私有 <c>ResolveOkTarget</c>/<c>StemOf</c>/<c>CandidateOkPaths</c>。
/// </summary>
public static class OkTargetResolver
{
    /// <summary>路径的文件名部分（去目录前缀 + 去 <c>.ok</c> 扩展名）= 图节点/知识对象显示名称。</summary>
    public static string StemOf(string path)
    {
        var sep = path.LastIndexOfAny(new[] { '/', '\\' });
        var stem = sep >= 0 ? path.Substring(sep + 1) : path;
        return stem.EndsWith(".ok", StringComparison.OrdinalIgnoreCase) && stem.Length > 3
            ? stem.Substring(0, stem.Length - 3) : stem;
    }

    /// <summary>候选 <c>.ok</c> 文件全路径（按优先序）：相对项目根 / 相对当前文件目录 / 原样（兜底绝对或相对 cwd）。</summary>
    public static IEnumerable<string> CandidateOkPaths(string targetPath, string? rootPath, string? currentFileDir)
    {
        var withExt = targetPath.EndsWith(".ok", StringComparison.OrdinalIgnoreCase)
            ? targetPath : targetPath + ".ok";
        if (!string.IsNullOrEmpty(rootPath))
            yield return Path.Combine(rootPath, withExt);
        if (!string.IsNullOrEmpty(currentFileDir))
            yield return Path.Combine(currentFileDir!, withExt);
        yield return withExt; // 兜底：绝对路径或相对 cwd
    }

    /// <summary>首个存在的候选 <c>.ok</c> 文件全路径；找不到返 null。</summary>
    private static string? TryFindOkFile(string targetPath, string? rootPath, string? currentFileDir)
    {
        foreach (var full in CandidateOkPaths(targetPath, rootPath, currentFileDir))
        {
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>解析 link target 到 (显示名称=stem, 定义)。首个存在 <c>.ok</c> → 读 + TryParse；
    /// 找不到文件 → (stem, null)（断链仍画叶子节点，不崩）。读/解析失败 → 仅名称不跟出边。空白路径 → null（无 link）。
    /// 供图闭包构建（跟出边需定义）。</summary>
    public static (string Title, KnowledgeObjectDefinition? Def)? ResolveTarget(
        IDocumentKnowledgeSourceParser parser, string? targetPath, string? rootPath, string? currentFileDir)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        var stem = StemOf(targetPath);
        var full = TryFindOkFile(targetPath, rootPath, currentFileDir);
        if (full is null) return (stem, null);
        KnowledgeObjectDefinition? def = null;
        try { _ = parser.TryParse(File.ReadAllText(full), out def, out _); }
        catch { /* 读/解析失败 → 仅名称，不跟出边（断链仍画节点显示 stem） */ }
        return (stem, def);
    }

    /// <summary>解析 link target 到 (stem, 原始文件内容)。供 <c>FormatAsync</c> 内联目标 <c>.ok</c> 原文
    /// （镜像 note 版 <c>FormatAsync</c> 内联 <c>target.Content</c>——文档版"原始 Content"= 目标 <c>.ok</c> 文件全文）。
    /// 首个存在 <c>.ok</c> → <see cref="File.ReadAllText"/>；找不到/读失败 → (stem, null)。空白路径 → null。</summary>
    public static (string Title, string? RawContent)? ResolveRaw(
        string? targetPath, string? rootPath, string? currentFileDir)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return null;
        var stem = StemOf(targetPath);
        var full = TryFindOkFile(targetPath, rootPath, currentFileDir);
        if (full is null) return (stem, null);
        try { return (stem, File.ReadAllText(full)); }
        catch { return (stem, null); }
    }
}
