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
}
