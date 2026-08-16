using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Acme.Desktop.Mvvm.Syntax;
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
            // 徽标排序对齐 VSCode：HEAD → 本地分支 → 远端分支 → 标签（同组按名）。
            var sorted = (refs is null || refs.Count == 0) ? Array.Empty<CommitRef>()
                : refs.OrderBy(RefOrder).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
            list.Add(new CommitNode(info, parents, sorted));
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

    public RemoteStatus GetRemoteStatus(string repoPath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return new RemoteStatus(null, 0, 0, false, null);

        var remoteUrl = repo.Network.Remotes.FirstOrDefault(r => r.Name == "origin")?.Url
                         ?? repo.Network.Remotes.FirstOrDefault()?.Url;
        var head = repo.Head;
        if (head is null || !head.IsTracking)
            return new RemoteStatus(null, 0, 0, false, remoteUrl);

        var td = head.TrackingDetails;
        return new RemoteStatus(head.UpstreamBranchCanonicalName, td.AheadBy ?? 0, td.BehindBy ?? 0, true, remoteUrl);
    }

    public bool HasSystemCredentials(string repoPath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return false;
        return TryGetCredentialsFromHelper(GetRemoteUrl(repo)) is not null;
    }

    public (bool Success, string Message) Fetch(string repoPath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return (false, "未找到 git 仓库。");
        var creds = ResolveCredentials(GetRemoteUrl(repo));
        if (creds is null) return (false, "未配置 GitHub 凭证：系统 git credential helper（macOS keychain / Windows Credential Manager / Linux libsecret）未返回凭证。请在终端执行一次 git push 让 git 保存凭证（macOS 存入 keychain），或确认系统已配 credential helper。");

        var remote = repo.Network.Remotes.FirstOrDefault(r => r.Name == "origin") ?? repo.Network.Remotes.FirstOrDefault();
        if (remote is null) return (false, "仓库未配置远程（origin）。");
        try
        {
            LibGit2Sharp.Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(r => r.Specification),
                new FetchOptions { CredentialsProvider = (_, _, _) => creds }, null);
            return (true, "已拉取远程更新。");
        }
        catch (Exception ex) { return (false, $"拉取失败：{ex.Message}"); }
    }

    public (bool Success, string Message) Pull(string repoPath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return (false, "未找到 git 仓库。");
        var creds = ResolveCredentials(GetRemoteUrl(repo));
        if (creds is null) return (false, "未配置 GitHub 凭证：系统 git credential helper（macOS keychain / Windows Credential Manager / Linux libsecret）未返回凭证。请在终端执行一次 git push 让 git 保存凭证（macOS 存入 keychain），或确认系统已配 credential helper。");
        var head = repo.Head;
        if (head is null || !head.IsTracking)
            return (false, "当前分支未设置上游跟踪，无法 pull（请先 push 建立跟踪）。");
        if (repo.RetrieveStatus().IsDirty)
            return (false, "工作区有未提交改动，请先提交或暂存后再 pull。");

        var sig = new Signature("Aether", "aether@local", DateTimeOffset.Now);
        try
        {
            var result = LibGit2Sharp.Commands.Pull(repo, sig, new PullOptions
            {
                FetchOptions = new FetchOptions { CredentialsProvider = (_, _, _) => creds },
                MergeOptions = new MergeOptions { FailOnConflict = false },
            });
            return result.Status switch
            {
                MergeStatus.UpToDate => (true, "已是最新，无需更新。"),
                MergeStatus.FastForward => (true, "已快进合并远程更新。"),
                MergeStatus.NonFastForward => (true, "已合并远程更新（三方合并）。"),
                MergeStatus.Conflicts => (false, "拉取完成但有冲突，请手动解决：\n" +
                    string.Join("\n", repo.Index.Conflicts.Select(c => c.Ancestor?.Path ?? c.Ours?.Path ?? c.Theirs?.Path ?? "?"))),
                _ => (true, $"拉取完成（{result.Status}）。"),
            };
        }
        catch (Exception ex) { return (false, $"拉取失败：{ex.Message}"); }
    }

    public (bool Success, string Message) Push(string repoPath)
    {
        using var repo = Open(repoPath);
        if (repo is null) return (false, "未找到 git 仓库。");
        var creds = ResolveCredentials(GetRemoteUrl(repo));
        if (creds is null) return (false, "未配置 GitHub 凭证：系统 git credential helper（macOS keychain / Windows Credential Manager / Linux libsecret）未返回凭证。请在终端执行一次 git push 让 git 保存凭证（macOS 存入 keychain），或确认系统已配 credential helper。");
        var head = repo.Head;
        if (head is null || head.Tip is null) return (false, "当前分支无提交，无法 push。");

        var pushOpts = new PushOptions { CredentialsProvider = (_, _, _) => creds };
        try
        {
            if (head.IsTracking)
            {
                repo.Network.Push(head, pushOpts);
            }
            else
            {
                var remote = repo.Network.Remotes.FirstOrDefault(r => r.Name == "origin") ?? repo.Network.Remotes.FirstOrDefault();
                if (remote is null) return (false, "仓库未配置远程（origin）。");
                var refspec = $"+{head.CanonicalName}:refs/heads/{head.FriendlyName}";
                repo.Network.Push(remote, refspec, pushOpts);
                // 建立上游跟踪，后续 pull/push 无需再指定。
                repo.Branches.Update(head, b => b.TrackedBranch = $"{remote.Name}/{head.FriendlyName}");
            }
            return (true, $"已推送 {head.FriendlyName} 到远程。");
        }
        catch (Exception ex) { return (false, $"推送失败：{ex.Message}"); }
    }

    // —— 远程凭证 ——
    // 仅复用系统 git credential helper（macOS keychain / Windows Credential Manager / Linux libsecret
    // 等已存凭证，VSCode 式免填）。不在应用内存储 PAT——未配置时引导用户在终端配 credential helper。

    /// <summary>解析 GitHub 凭证：复用系统 git credential helper 已存凭证。未配置返回 null（上层引导用户配 helper）。</summary>
    private static UsernamePasswordCredentials? ResolveCredentials(string? url)
        => TryGetCredentialsFromHelper(url);

    /// <summary>调系统 <c>git credential fill</c> 读 credential helper（如 macOS keychain）已存的凭证。免填 PAT。</summary>
    /// <param name="url">远程 URL（push/fetch 时的实际地址）；null 时用 protocol=https + host=github.com。</param>
    private static UsernamePasswordCredentials? TryGetCredentialsFromHelper(string? url)
    {
        var git = FindGitExecutable();
        if (git is null) return null;
        try
        {
            var input = string.IsNullOrEmpty(url)
                ? "protocol=https\nhost=github.com\n\n"
                : $"url={url}\n\n";
            var psi = new ProcessStartInfo
            {
                FileName = git,
                Arguments = "credential fill",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // 禁止交互式提示（askpass / 终端），避免进程挂起等待输入。keychain/libsecret/manager 均非交互。
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { /* 忽略 */ } return null; }
            if (p.ExitCode != 0) return null;

            string? user = null, pass = null;
            foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("username=")) user = line["username=".Length..];
                else if (line.StartsWith("password=")) pass = line["password=".Length..];
            }
            if (string.IsNullOrEmpty(pass)) return null;
            return new UsernamePasswordCredentials { Username = string.IsNullOrEmpty(user) ? "token" : user!, Password = pass! };
        }
        catch { return null; }
    }

    /// <summary>定位系统 git 可执行文件。.app 启动 PATH 受限时逐个尝试常见安装路径兜底。</summary>
    private static string? FindGitExecutable()
    {
        // PATH 里的 git（从终端 dotnet run 启动时有）。
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var full = Path.Combine(dir.Trim('"'), "git");
                    if (File.Exists(full)) return full;
                }
                catch { /* 忽略非法路径段 */ }
            }
        }
        // 常见安装路径兜底（macOS Xcode CLI / Apple Silicon homebrew / Intel homebrew / Linux）。
        foreach (var candidate in new[] { "/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>当前分支跟踪的远程 URL（无跟踪时回退 origin/首个远程）。用于凭证解析的 url 参数。</summary>
    private static string? GetRemoteUrl(Repository repo)
    {
        var head = repo.Head;
        var remoteName = !string.IsNullOrEmpty(head?.RemoteName) ? head!.RemoteName : null;
        var remote = remoteName is not null ? repo.Network.Remotes[remoteName] : null;
        return (remote ?? repo.Network.Remotes.FirstOrDefault(r => r.Name == "origin")
                       ?? repo.Network.Remotes.FirstOrDefault())?.Url;
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

    /// <summary>徽标排序键：HEAD(0) → 本地分支(1) → 远端分支(2) → 标签(3)。</summary>
    private static int RefOrder(CommitRef r) => r.IsHead ? 0 : r.IsTag ? 3 : r.IsRemote ? 2 : 1;

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

    /// <summary>扩展名 → 语法 key。转发 <see cref="SyntaxMap.SyntaxForExtension"/>（单一来源；旧版本地镜像已删）。
    /// internal 供 <see cref="AgentEditPreview"/> 复用（agent 编辑审批 diff 也需按扩展名上色）。</summary>
    internal static string? SyntaxForExtension(string? ext) => SyntaxMap.SyntaxForExtension(ext);
}
