using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 知识对象递归展开器（纯函数，可单测）。把一个 <see cref="KnowledgeObjectDefinition"/> 组装成
/// 「path + kind + name + 正文 + links（标了 <see cref="KnowledgeLink.Expand"/> 的边递归内联目标内容）」的文本，
/// 供 AI 工具 <c>read_document_knowledge_object</c> 喂 LLM 上下文。
/// <para><b>两种模式</b>（<paramref name="expandDepth"/> 控制）：</para>
/// <list type="bullet">
/// <item><c>null</c>（默认）= <b>作者标志模式</b>：只内联 <see cref="KnowledgeLink.Expand"/>=true 的边，沿标过的链递归展开（多层），
/// budget=<see cref="MaxExpandDepth"/> 兜底防链过长。未标的边只列在 links 清单（agent 按需单独 read）。<b>默认翻转</b>：
/// 既有文件无 expand 字段 → 全 false → 只返回当前对象本身（body + links 清单），契合「当前页本身才重要」。</item>
/// <item>非 null = <b>覆盖模式</b>（agent 临时挖深）：忽略作者标志，blanket 内联 N 层全部子节点
/// （<see cref="Math.Min"/>(N, <see cref="MaxExpandDepth"/>) 兜底）。N=0 仅本对象。</item>
/// </list>
/// <para><b>安全阀</b>：递归深度硬上限 <see cref="MaxExpandDepth"/>；单目标正文上限 <see cref="MaxTargetInlineChars"/>；
/// 累计内联正文上限 <see cref="MaxLinkedInlineTotalChars"/>；按全路径去重防环（菱形依赖只展开一次）。
/// 解析目标经注入的 <paramref name="resolve"/> 函数（工具包 <see cref="OkTargetResolver.ResolveTargetWithPath"/>，
/// 测试可注入 fake），故本器本身无文件 I/O、确定性可测。</para>
/// </summary>
public static class KnowledgeExpansionBuilder
{
    /// <summary>递归展开深度硬上限（防标过的链过长 / 覆盖参数过大撑爆上下文）。</summary>
    public const int MaxExpandDepth = 4;

    /// <summary>单个目标正文内联上限（字段表/schema 信息密度高，截断头部）。</summary>
    public const int MaxTargetInlineChars = 8000;

    /// <summary>所有内联正文累计上限（防多目标累积撑爆 LLM 上下文窗口）。</summary>
    public const int MaxLinkedInlineTotalChars = 20000;

    /// <param name="def">焦点知识对象定义。</param>
    /// <param name="relPath">焦点相对路径（写进 <c>path:</c> 行，agent 据此再调工具定位）。</param>
    /// <param name="focalFullPath">焦点磁盘全路径（去重键种子，防子节点回链焦点造成环；可空=不防环兜底靠深度上限）。</param>
    /// <param name="resolve">link target → <see cref="ResolvedKnowledgeTarget"/>（显示名+全路径+定义）。空白路径返 null 跳过。</param>
    /// <param name="expandDepth">null=作者标志模式；非 null=覆盖模式 blanket N 层。</param>
    public static string BuildExpandedDefinition(
        KnowledgeObjectDefinition def,
        string relPath,
        string? focalFullPath,
        Func<string?, string?, ResolvedKnowledgeTarget?> resolve,
        int? expandDepth)
    {
        var sb = new StringBuilder();
        var followAll = expandDepth.HasValue;
        var budget = expandDepth.HasValue ? Math.Min(expandDepth.Value, MaxExpandDepth) : MaxExpandDepth;
        var focalDir = focalFullPath is not null
            ? (Path.GetDirectoryName(focalFullPath) ?? string.Empty)
            : string.Empty;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (focalFullPath is not null) visited.Add(focalFullPath);
        var linkedTotal = 0;
        AppendNode(sb, def, relPath, focalDir, "", budget, followAll, resolve, ref linkedTotal, visited);
        return sb.ToString().TrimEnd();
    }

    private static void AppendNode(
        StringBuilder sb, KnowledgeObjectDefinition def, string targetPath, string currentFileDir,
        string indent, int remainingDepth, bool followAll,
        Func<string?, string?, ResolvedKnowledgeTarget?> resolve,
        ref int linkedTotal, HashSet<string> visited)
    {
        // —— 头部 ——
        sb.Append(indent).Append("path: ").AppendLine(targetPath);
        sb.Append(indent).Append("kind: ").AppendLine(def.Kind);
        // front-matter 透传字段（kind/links 之外——如 Intelligence 生命周期 subject/claim/status）：
        // agent 读认知对象时须能看到这些元数据（修复前只回 kind+正文，生命周期信息对 agent 不可见）。
        if (def.ExtraFrontMatter is { Count: > 0 })
        {
            foreach (var (key, value) in def.ExtraFrontMatter)
                sb.Append(indent).Append(key).Append(": ").AppendLine(value?.ToString() ?? "");
        }
        sb.Append(indent).Append("name: ").AppendLine(OkTargetResolver.StemOf(targetPath));

        // —— 正文（单目标截断 + 累计计数） ——
        if (!string.IsNullOrWhiteSpace(def.Body))
        {
            var body = def.Body.TrimEnd();
            if (body.Length > MaxTargetInlineChars)
            {
                body = body[..MaxTargetInlineChars]
                    + $"\n…（已截断，原文共 {def.Body.Length} 字符；如需完整内容请单独 ReadDocumentKnowledgeObject \"{targetPath}\"）";
            }
            linkedTotal += body.Length;
            sb.AppendLine();
            sb.Append(Indent(body, indent));
            sb.AppendLine();
        }

        // —— links ——
        if (def.Links is not { Count: > 0 }) return;

        sb.AppendLine();
        sb.Append(indent).AppendLine("links:");
        foreach (var l in def.Links)
        {
            var resolved = resolve(l.Target, currentFileDir);
            if (resolved is null) continue; // 空白路径，跳过
            var (title, childFull, childDef) = resolved;
            var marker = l.Expand ? " [expand]" : string.Empty;
            sb.Append(indent).Append($"- [{l.Role}] {title} (path:{l.Target}){marker}");

            var wantInline = followAll || l.Expand;
            if (!wantInline)
            {
                // 作者标志模式 + 未标：只列引用，不内联（默认行为，无提示，保持清单紧凑）
                sb.AppendLine();
                continue;
            }

            // 要内联：检查各安全阀，命中则只列名 + 简短原因
            sb.AppendLine(":");
            if (remainingDepth <= 0)
            {
                sb.Append(indent).AppendLine("  （已达展开深度上限，如需完整内容请单独 ReadDocumentKnowledgeObject 对应路径）");
                continue;
            }
            if (linkedTotal >= MaxLinkedInlineTotalChars)
            {
                sb.Append(indent).AppendLine("  （引用过多，已停止展开后续目标；完整内容请单独 ReadDocumentKnowledgeObject 对应路径）");
                continue;
            }
            if (childFull is not null && visited.Contains(childFull))
            {
                sb.Append(indent).AppendLine("  （已在展开路径上，跳过防环）");
                continue;
            }
            if (childDef is null)
            {
                sb.Append(indent).AppendLine("  （找不到知识对象文件）");
                continue;
            }

            // 内联：递归子节点（缩进 +2，剩余深度 -1）
            if (childFull is not null) visited.Add(childFull);
            var childDir = childFull is not null
                ? (Path.GetDirectoryName(childFull) ?? currentFileDir)
                : currentFileDir;
            AppendNode(sb, childDef, l.Target, childDir, indent + "  ",
                remainingDepth - 1, followAll, resolve, ref linkedTotal, visited);
        }
    }

    /// <summary>每行加前缀（正文/内联子节点缩进）。</summary>
    private static string Indent(string text, string prefix)
    {
        if (string.IsNullOrEmpty(text)) return prefix;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = prefix + lines[i];
        return string.Join('\n', lines);
    }
}
