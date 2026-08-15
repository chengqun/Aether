using System;
using System.Collections.Generic;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// 由旧/新两段文本经 LCS 行对齐组装 <see cref="DiffModel"/>（不依赖 LibGit2Sharp）。
/// 供 agent 文件编辑审批卡等「无 git patch、只有改前/改后文本」的场景复用。
/// SCM git diff 仍走 <see cref="GitService"/> 的 unified-patch 解析路径，互不干扰。
/// </summary>
public static class LineDiffBuilder
{
    /// <summary>超过此格点数（m*n）退化为整文件对齐，避免大文件 O(m*n) 动规爆内存/卡顿。</summary>
    private const long LcsCellBudget = 5_000_000;

    /// <summary>由旧/新文本构建左右并排 diff。二进制（含 \0）→ IsBinary。</summary>
    public static DiffModel Build(string? oldText, string? newText, string filePath, string? syntaxKey)
    {
        oldText ??= string.Empty;
        newText ??= string.Empty;

        if (IsBinary(oldText) || IsBinary(newText))
            return new DiffModel(oldText, newText, Array.Empty<DiffLine>(), Array.Empty<DiffLine>(), IsBinary: true)
            { SyntaxKey = syntaxKey, FilePath = filePath };

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        // 空 old / 空 new 特例：全 Added / 全 Removed（与 GitService.FallbackDiff 语义一致）。
        if (oldLines.Length == 0 && newLines.Length == 0)
            return new DiffModel(oldText, newText, Array.Empty<DiffLine>(), Array.Empty<DiffLine>(), IsBinary: false)
            { SyntaxKey = syntaxKey, FilePath = filePath };

        IReadOnlyList<DiffLine> oldOut, newOut;
        if ((long)oldLines.Length * newLines.Length > LcsCellBudget)
            (oldOut, newOut) = NaiveAlign(oldLines, newLines);
        else
            (oldOut, newOut) = LcsAlign(oldLines, newLines);

        return new DiffModel(oldText, newText, oldOut, newOut, IsBinary: false)
        { SyntaxKey = syntaxKey, FilePath = filePath };
    }

    /// <summary>LCS 动规 + 回溯产出对齐行。Equal→两侧 Context；Delete→左 Removed/右 Blank；Insert→左 Blank/右 Added。</summary>
    private static (IReadOnlyList<DiffLine> Old, IReadOnlyList<DiffLine> New) LcsAlign(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = 1; i <= m; i++)
        for (var j = 1; j <= n; j++)
            dp[i, j] = a[i - 1] == b[j - 1]
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

        // 回溯收集编辑操作（逆序），再翻正。
        var ops = new List<EditOp>(m + n);
        {
            var i = m; var j = n;
            while (i > 0 && j > 0)
            {
                if (a[i - 1] == b[j - 1]) { ops.Add(new EditOp(EditType.Equal, a[i - 1])); i--; j--; }
                else if (dp[i - 1, j] >= dp[i, j - 1]) { ops.Add(new EditOp(EditType.Delete, a[i - 1])); i--; }
                else { ops.Add(new EditOp(EditType.Insert, b[j - 1])); j--; }
            }
            while (i > 0) { ops.Add(new EditOp(EditType.Delete, a[i - 1])); i--; }
            while (j > 0) { ops.Add(new EditOp(EditType.Insert, b[j - 1])); j--; }
        }
        ops.Reverse();

        var oldOut = new List<DiffLine>(ops.Count);
        var newOut = new List<DiffLine>(ops.Count);
        foreach (var op in ops)
        {
            switch (op.Type)
            {
                case EditType.Equal:
                    oldOut.Add(new DiffLine(op.Text, DiffLineKind.Context));
                    newOut.Add(new DiffLine(op.Text, DiffLineKind.Context));
                    break;
                case EditType.Delete:
                    oldOut.Add(new DiffLine(op.Text, DiffLineKind.Removed));
                    newOut.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
                    break;
                case EditType.Insert:
                    oldOut.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
                    newOut.Add(new DiffLine(op.Text, DiffLineKind.Added));
                    break;
            }
        }
        return (oldOut, newOut);
    }

    /// <summary>大文件退化：不对齐，旧侧全 Removed、新侧全 Added（仍可读，仅无逐行对齐）。</summary>
    private static (IReadOnlyList<DiffLine> Old, IReadOnlyList<DiffLine> New) NaiveAlign(string[] a, string[] b)
    {
        var oldOut = new List<DiffLine>(a.Length);
        var newOut = new List<DiffLine>(b.Length);
        foreach (var t in a) oldOut.Add(new DiffLine(t, DiffLineKind.Removed));
        foreach (var t in b) newOut.Add(new DiffLine(t, DiffLineKind.Added));
        // 补齐两侧长度（Blank 占位），让左右双编辑器行对齐。
        var max = Math.Max(oldOut.Count, newOut.Count);
        while (oldOut.Count < max) oldOut.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
        while (newOut.Count < max) newOut.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
        return (oldOut, newOut);
    }

    private static string[] SplitLines(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length == 0) return Array.Empty<string>();
        return text.Split('\n');
    }

    private static bool IsBinary(string text) => text.Contains('\0');

    private enum EditType { Equal, Delete, Insert }

    private readonly record struct EditOp(EditType Type, string Text);
}
