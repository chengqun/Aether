using System.Collections.Generic;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>git 源代码管理服务（LibGit2Sharp 封装）。所有方法以 <paramref name="repoPath"/> 定位仓库（可为工作区子目录，内部 Discover 找 .git 根）。
/// 无仓库 / 路径无效时方法返回空结果或 false，不抛——上层据 <see cref="IsRepository"/> 决定空态。</summary>
public interface IGitService
{
    /// <summary>路径是否在某个 git 仓库内。</summary>
    bool IsRepository(string path);

    /// <summary>Discover .git 根（<see cref="LibGit2Sharp.Repository.Discover"/>）；非仓库返回 null。</summary>
    string? Discover(string path);

    /// <summary>初始化新 git 仓库（<c>git init</c>）。默认分支 <c>main</c>（对齐现代 Git/GitHub）。
    /// 路径无效/已存在仓库时返回 false 不抛。成功后上层据 <see cref="IsRepository"/> 重新加载。</summary>
    bool Init(string repoPath);

    /// <summary>取仓库所有文件变更（staged + unstaged 合并，<see cref="GitChange.IsStaged"/> 区分）。</summary>
    IReadOnlyList<GitChange> GetStatus(string repoPath);

    /// <summary>取某文件 HEAD vs 工作区的左右并排 diff。二进制/无变化返回 null。</summary>
    DiffModel? GetDiff(string repoPath, string filePath);

    /// <summary>暂存文件（index add）。</summary>
    void Stage(string repoPath, string filePath);

    /// <summary>取消暂存（index reset 到 HEAD）。</summary>
    void Unstage(string repoPath, string filePath);

    /// <summary>丢弃工作区改动（恢复到 HEAD）；untracked 文件删除。</summary>
    void Discard(string repoPath, string filePath);

    /// <summary>提交暂存区。返回提交是否成功。</summary>
    bool Commit(string repoPath, string message);

    /// <summary>取提交历史（拓扑序，newest 在前，限 <paramref name="maxCount"/> 条）+ 每 commit 的父 sha + 指向它的分支/标签。</summary>
    IReadOnlyList<CommitNode> GetHistory(string repoPath, int maxCount);

    /// <summary>取某 commit 相对其首父的改动文件列表。</summary>
    IReadOnlyList<CommitFileChange> GetCommitChanges(string repoPath, string sha);

    /// <summary>取某 commit 某文件 相对首父的左右并排 diff（parent 旧 / commit 新）。二进制/无变化返回 null。</summary>
    DiffModel? GetCommitFileDiff(string repoPath, string sha, string filePath);

    /// <summary>当前分支相对上游的领先/落后状态（本地计算，不联网）。</summary>
    RemoteStatus GetRemoteStatus(string repoPath);

    /// <summary>系统 git credential helper（如 macOS keychain）是否已存有当前远程的凭证（免填 PAT 判断）。不联网，仅查 helper。</summary>
    bool HasSystemCredentials(string repoPath);

    /// <summary>从远程拉取更新（fetch）但不合并。凭证复用系统 git credential helper（不读应用内存储）。</summary>
    (bool Success, string Message) Fetch(string repoPath);

    /// <summary>拉取并合并远程更新到当前分支。可能产生冲突——Message 含冲突文件列表。</summary>
    (bool Success, string Message) Pull(string repoPath);

    /// <summary>推送当前分支到上游（无上游时推到 origin 并设置跟踪）。不可逆。</summary>
    (bool Success, string Message) Push(string repoPath);
}
