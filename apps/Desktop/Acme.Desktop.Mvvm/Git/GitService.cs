using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace Acme.Desktop.Mvvm.Git;

/// <summary><see cref="IGitService"/> 的 LibGit2Sharp 实现。所有 git 操作封装在此，VM/视图无 LibGit2Sharp 直接依赖。
/// native lib（osx/linux/win）随 LibGit2Sharp runtimes 部署，不依赖系统 git CLI（避 .app PATH 受限坑）。</summary>
public sealed class GitService : IGitService
{
    public bool IsRepository(string path) => Discover(path) is not null;

    public string? Discover(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Repository.Discover(path); }
        catch { return null; }
    }

    public IReadOnlyList<GitChange> GetStatus(string repoPath)
    {
        var list = new List<GitChange>();
        using var repo = Open(repoPath);
        if (repo is null) return list;

        var status = repo.RetrieveStatus();
        foreach (var e in status)
        {
            var (kind, staged) = Classify(e.State);
            if (kind is null) continue;
            list.Add(new GitChange(e.FilePath, kind.Value, staged));
        }
        // 文件名排序，稳定显示。
        return list.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DiffModel? GetDiff(string repoPath, string filePath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return null;

        var fullPath = Path.Combine(repo.Info.WorkingDirectory, filePath);
        var syntaxKey = SyntaxForExtension(Path.GetExtension(filePath));

        // 新文件（HEAD 无此条目）：左空右满，全 Added。
        var oldText = HeadBlob(repo, filePath) ?? string.Empty;
        var newText = File.Exists(fullPath) ? ReadText(fullPath) : string.Empty;

        // 取 unified diff patch（HEAD tree vs 工作区）。
        var patch = ComparePatch(repo, filePath);
        return BuildDiffModel(oldText, newText, patch, filePath, syntaxKey);
    }

    public void Stage(string repoPath, string filePath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return;
        LibGit2Sharp.Commands.Stage(repo, filePath);
    }

    public void Unstage(string repoPath, string filePath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return;
        LibGit2Sharp.Commands.Unstage(repo, filePath);
    }

    public void Discard(string repoPath, string filePath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return;
        var fullPath = Path.Combine(repo.Info.WorkingDirectory, filePath);
        // untracked 文件无 HEAD 版本可恢复 → 直接删。
        if (repo.RetrieveStatus(filePath).HasFlag(FileStatus.NewInWorkdir))
        {
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* 忽略 */ }
            return;
        }
        // 已跟踪：从 HEAD 检出覆盖工作区。
        repo.CheckoutPaths(repo.Head.Tip.Sha, new[] { filePath }, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
    }

    public bool Commit(string repoPath, string message)
    {
        using var repo = Open(repoPath);
        if (repo is null) return false;
        var sig = new Signature("Aether", "aether@local", DateTimeOffset.Now);
        var r = repo.Commit(message, sig, sig);
        return r is not null;
    }

    public IReadOnlyList<CommitNode> GetHistory(string repoPath, int maxCount)
    {
        var list = new List<CommitNode>();
        using var repo = Open(repoPath);
        if (repo is null) return list;

        var refsBySha = BuildRefsMap(repo);
        var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        foreach (var c in repo.Commits.QueryBy(filter).Take(maxCount))
        {
            var sha = c.Sha;
            var info = new CommitInfo(
                sha,
                sha.Length >= 7 ? sha[..7] : sha,
                c.MessageShort ?? string.Empty,
                c.Author?.Name ?? string.Empty,
                c.Author?.When ?? DateTimeOffset.Now);
            var parents = c.Parents.Select(p => p.Sha).ToArray();
            refsBySha.TryGetValue(sha, out var refs);
            list.Add(new CommitNode(info, parents, (IReadOnlyList<CommitRef>?)refs ?? Array.Empty<CommitRef>()));
        }
        return list;
    }

    public IReadOnlyList<CommitFileChange> GetCommitChanges(string repoPath, string sha)
    {
        var list = new List<CommitFileChange>();
        using var repo = Open(repoPath);
        if (repo is null) return list;
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return list;

        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
        foreach (var ch in changes)
            list.Add(new CommitFileChange(ch.Path, MapTreeChange(ch.Status)));
        return list.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public DiffModel? GetCommitFileDiff(string repoPath, string sha, string filePath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return null;
        var commit = repo.Lookup<Commit>(sha);
        if (commit is null) return null;

        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        var commitTree = commit.Tree;
        var syntaxKey = SyntaxForExtension(Path.GetExtension(filePath));
        var oldText = TreeBlobText(parentTree, filePath);
        var newText = TreeBlobText(commitTree, filePath);
        var patch = CompareTreePatch(repo, parentTree, commitTree, filePath);
        return BuildDiffModel(oldText, newText, patch, filePath, syntaxKey);
    }

    // —— helpers ——

    /// <summary>由旧/新文本 + unified patch 文本组装 <see cref="DiffModel"/>（二进制检测 + 解析对齐行）。
    /// <see cref="GetDiff"/> 与 <see cref="GetCommitFileDiff"/> 共用。</summary>
    private static DiffModel BuildDiffModel(string oldText, string newText, string? patchText, string filePath, string? syntaxKey)
    {
        if (IsBinary(oldText) || IsBinary(newText))
            return new DiffModel(oldText, newText, Array.Empty<DiffLine>(), Array.Empty<DiffLine>(), IsBinary: true)
            { SyntaxKey = syntaxKey, FilePath = filePath };

        var (oldLines, newLines) = patchText is not null
            ? ParseUnifiedDiff(patchText)
            : FallbackDiff(oldText, newText);
        return new DiffModel(oldText, newText, oldLines, newLines, IsBinary: false)
        { SyntaxKey = syntaxKey, FilePath = filePath };
    }

    /// <summary>建 sha → 指向它的分支/标签 映射（IsHead=当前 HEAD 分支，IsRemote=远端分支，IsTag=标签）。</summary>
    private static Dictionary<string, List<CommitRef>> BuildRefsMap(Repository repo)
    {
        var map = new Dictionary<string, List<CommitRef>>(StringComparer.OrdinalIgnoreCase);
        var headName = repo.Head?.CanonicalName;

        void Add(string sha, CommitRef r)
        {
            if (!map.TryGetValue(sha, out var list)) { list = new List<CommitRef>(); map[sha] = list; }
            list.Add(r);
        }

        foreach (var b in repo.Branches)
        {
            if (b.Tip is null) continue;
            var isRemote = b.IsRemote;
            var isHead = !isRemote && b.CanonicalName == headName;
            Add(b.Tip.Sha, new CommitRef(b.FriendlyName, isHead, isRemote, false));
        }
        foreach (var t in repo.Tags)
        {
            if (t.Target is Commit tc)
                Add(tc.Sha, new CommitRef(t.FriendlyName, false, false, true));
        }
        return map;
    }

    private static ChangeKind MapTreeChange(LibGit2Sharp.ChangeKind s) => s switch
    {
        LibGit2Sharp.ChangeKind.Added => ChangeKind.Added,
        LibGit2Sharp.ChangeKind.Deleted => ChangeKind.Deleted,
        LibGit2Sharp.ChangeKind.Renamed => ChangeKind.Renamed,
        LibGit2Sharp.ChangeKind.Modified => ChangeKind.Modified,
        _ => ChangeKind.Modified,
    };

    private static string TreeBlobText(Tree? tree, string filePath)
    {
        if (tree is null) return string.Empty;
        var entry = tree[filePath];
        if (entry?.Target is Blob blob) return BlobToText(blob);
        return string.Empty;
    }

    private static string BlobToText(Blob blob)
    {
        using var ms = new MemoryStream();
        blob.GetContentStream().CopyTo(ms);
        return TryDecodeUtf8(ms.ToArray()) ?? string.Empty;
    }

    private static string? CompareTreePatch(Repository repo, Tree? oldTree, Tree newTree, string filePath)
    {
        try
        {
            var patch = repo.Diff.Compare<Patch>(oldTree, newTree, new[] { filePath });
            return patch?.FirstOrDefault()?.Patch;
        }
        catch { return null; }
    }

    private static Repository? Open(string repoPath)
    {
        var discovered = Repository.Discover(repoPath);
        if (discovered is null) return null;
        try { return new Repository(discovered); }
        catch { return null; }
    }

    /// <summary>FileStatus → (ChangeKind, IsStaged)。staged 优先（index 有改动算 staged）。</summary>
    private static (ChangeKind? Kind, bool Staged) Classify(FileStatus s)
    {
        // staged（index vs HEAD）
        if (s.HasFlag(FileStatus.NewInIndex)) return (ChangeKind.Added, true);
        if (s.HasFlag(FileStatus.ModifiedInIndex)) return (ChangeKind.Modified, true);
        if (s.HasFlag(FileStatus.DeletedFromIndex)) return (ChangeKind.Deleted, true);
        if (s.HasFlag(FileStatus.RenamedInIndex)) return (ChangeKind.Renamed, true);
        // unstaged（workdir vs index）
        if (s.HasFlag(FileStatus.NewInWorkdir)) return (ChangeKind.Untracked, false);
        if (s.HasFlag(FileStatus.ModifiedInWorkdir)) return (ChangeKind.Modified, false);
        if (s.HasFlag(FileStatus.DeletedFromWorkdir)) return (ChangeKind.Deleted, false);
        if (s.HasFlag(FileStatus.RenamedInWorkdir)) return (ChangeKind.Renamed, false);
        return (null, false);
    }

    private static string? HeadBlob(Repository repo, string filePath)
    {
        var entry = repo.Head?.Tip?[filePath];
        if (entry is null) return null;
        if (entry.Target is Blob blob)
        {
            using var ms = new MemoryStream();
            blob.GetContentStream().CopyTo(ms);
            return TryDecodeUtf8(ms.ToArray());
        }
        return null;
    }

    private static string? ComparePatch(Repository repo, string filePath)
    {
        try
        {
            var head = repo.Head?.Tip?.Tree;
            if (head is null) return null;
            var patch = repo.Diff.Compare<Patch>(head, DiffTargets.WorkingDirectory, new[] { filePath });
            return patch?.FirstOrDefault()?.Patch;
        }
        catch { return null; }
    }

    /// <summary>解析 unified diff patch 文本为对齐的左右行数组。</summary>
    private static (IReadOnlyList<DiffLine> Old, IReadOnlyList<DiffLine> New) ParseUnifiedDiff(string patch)
    {
        var oldLines = new List<DiffLine>();
        var newLines = new List<DiffLine>();
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        // 跳过文件头（--- +++）直到第一个 hunk。
        foreach (var line in lines)
        {
            if (line.StartsWith("@@")) continue;
            if (line.StartsWith("---") || line.StartsWith("+++")) continue;
            if (line.StartsWith("diff ") || line.StartsWith("index ")) continue;
            if (line.StartsWith("\\ ")) continue; // "\ No newline at end of file"

            if (line.StartsWith("-"))
            {
                oldLines.Add(new DiffLine(line[1..], DiffLineKind.Removed));
                newLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
            }
            else if (line.StartsWith("+"))
            {
                newLines.Add(new DiffLine(line[1..], DiffLineKind.Added));
                oldLines.Add(new DiffLine(string.Empty, DiffLineKind.Blank));
            }
            else
            {
                // context 行（可能以空格开头，也可能空行）
                var text = line.Length > 0 && line[0] == ' ' ? line[1..] : line;
                oldLines.Add(new DiffLine(text, DiffLineKind.Context));
                newLines.Add(new DiffLine(text, DiffLineKind.Context));
            }
        }
        return (oldLines, newLines);
    }

    /// <summary>无 patch 时（如新文件无 HEAD、或 patch 取失败）退化为整文件逐行对齐：旧/新各自全部 Context。</summary>
    private static (IReadOnlyList<DiffLine> Old, IReadOnlyList<DiffLine> New) FallbackDiff(string oldText, string newText)
    {
        var oldArr = oldText.Replace("\r\n", "\n").Split('\n');
        var newArr = newText.Replace("\r\n", "\n").Split('\n');
        // 若旧为空、新非空 → 全 Added；旧非空、新为空 → 全 Removed；否则按各自 Context（不对齐，仅展示全文）。
        if (string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(newText))
        {
            var nl = newArr.Select(t => new DiffLine(t, DiffLineKind.Added)).ToList();
            var ol = Enumerable.Repeat(new DiffLine(string.Empty, DiffLineKind.Blank), nl.Count).ToList();
            return (ol, nl);
        }
        if (!string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
        {
            var ol = oldArr.Select(t => new DiffLine(t, DiffLineKind.Removed)).ToList();
            var nl = Enumerable.Repeat(new DiffLine(string.Empty, DiffLineKind.Blank), ol.Count).ToList();
            return (ol, nl);
        }
        return (
            oldArr.Select(t => new DiffLine(t, DiffLineKind.Context)).ToList(),
            newArr.Select(t => new DiffLine(t, DiffLineKind.Context)).ToList());
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        catch { return null; }
    }

    private static bool IsBinary(string text) => text.Contains('\0');

    /// <summary>扩展名 → 语法 key（镜像 CodeEditorSetup.SyntaxForExtension，但 Mvvm 不能引 Shared）。</summary>
    private static string? SyntaxForExtension(string ext)
    {
        return ext.ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".py" => "python",
            ".js" => "javascript",
            ".ts" => "javascript",
            ".json" => "json",
            ".md" => "markdown",
            ".yaml" or ".yml" => "yaml",
            ".xml" or ".axaml" or ".xaml" => "xml",
            ".sql" => "sql",
            ".html" or ".htm" => "html",
            ".css" => "css",
            ".sh" => "shell",
            _ => null,
        };
    }
}
