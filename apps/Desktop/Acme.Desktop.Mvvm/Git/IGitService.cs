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

    /// <summary>把用户名+凭证存入系统 git credential helper（macOS keychain / Windows Credential Manager / Linux libsecret）——
    /// <c>git credential approve</c>（<see cref="HasSystemCredentials"/> 的写对称）。应用内不留存 PAT：仅 pipe 给 git 子进程 stdin，
    /// 存的是系统 helper（与终端 <c>git push</c> 存的是同一处），之后 <see cref="Push"/> 走 credential fill 免填。
    /// 用于绑定新远端首推时系统无凭证的场景。url/password 空返回失败。</summary>
    (bool Success, string Message) StoreCredentials(string url, string username, string password);

    /// <summary>从远程拉取更新（fetch）但不合并。凭证复用系统 git credential helper（不读应用内存储）。</summary>
    (bool Success, string Message) Fetch(string repoPath);

    /// <summary>拉取并合并远程更新到当前分支。可能产生冲突——Message 含冲突文件列表。</summary>
    (bool Success, string Message) Pull(string repoPath);

    /// <summary>推送当前分支到上游（无上游时推到 origin 并设置跟踪）。不可逆。</summary>
    (bool Success, string Message) Push(string repoPath);

    /// <summary>当前仓库的远端 URL（origin 优先，回退首个 remote；无 remote 返回 null）。用于绑定/换远端 UI 预填 + 显示。</summary>
    string? GetRemoteUrl(string repoPath);

    /// <summary>添加远端（默认名 origin；已存在同名则改其 URL，支持「换远端」）。本地操作，不联网。URL 空返回失败。</summary>
    (bool Success, string Message) AddRemote(string repoPath, string name, string url);

    /// <summary>重命名当前分支（<c>git branch -M</c>，强制移动 ref + 更新 HEAD 指向）。本地操作。newName 空/含空格返回失败（防 shell 注入）。</summary>
    (bool Success, string Message) RenameCurrentBranch(string repoPath, string newName);
}
