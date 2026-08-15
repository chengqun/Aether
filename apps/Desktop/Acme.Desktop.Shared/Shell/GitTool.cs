using System.ComponentModel;
using System.Text;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>git_status</c> / <c>git_commit</c> 工具：让 agent 查看与提交当前项目的 git 变更。
/// 经 <see cref="ICurrentProjectDirectoryProvider"/>（文档页回写）取真实仓库路径，操作封装在
/// <see cref="IGitService"/>（LibGit2Sharp）。与 <c>GetCurrentProjectDirectory</c> 同属宿主层注册的
/// AI 工具——git 无业务上下文，<see cref="GitService"/> 在宿主 VM 库，不走 ai/ 适配器。
/// </summary>
public sealed class GitTool
{
    private readonly IGitService _git;
    private readonly ICurrentProjectDirectoryProvider _projectDir;
    private readonly IGitRepositoryNotifier? _notifier;

    public GitTool(IGitService git, ICurrentProjectDirectoryProvider projectDir, IGitRepositoryNotifier? notifier = null)
    {
        _git = git;
        _projectDir = projectDir;
        _notifier = notifier;
    }

    /// <summary>一条变更记录（路径 + 状态字母 + 是否已暂存）。</summary>
    public sealed class ChangeRecord
    {
        public string Path { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;   // M/A/D/U/R
        public bool IsStaged { get; set; }
    }

    public sealed class StatusOutput
    {
        public bool IsRepository { get; set; }
        public string? ProjectPath { get; set; }
        /// <summary>已暂存变更（index vs HEAD）。</summary>
        public List<ChangeRecord> Staged { get; set; } = new();
        /// <summary>未暂存变更（工作区 vs index）。</summary>
        public List<ChangeRecord> Unstaged { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class CommitOutput
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        /// <summary>本次提交的文件数。</summary>
        public int FilesCommitted { get; set; }
        public string? Error { get; set; }
    }

    [Description("查看当前项目（文档页打开的目录）的 git 工作区状态：已暂存 / 未暂存的变更文件列表。只读，无副作用。" +
                 "提交前先用此工具确认有哪些变更。未打开 git 仓库时返回 IsRepository=false。")]
    public Task<StatusOutput> GitStatus(CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
            return Task.FromResult(new StatusOutput { IsRepository = false, Error = "当前未打开 git 项目目录。" });

        var status = _git.GetStatus(dir);
        var output = new StatusOutput { IsRepository = true, ProjectPath = dir };
        foreach (var c in status)
        {
            var rec = new ChangeRecord { Path = c.Path, Status = Letter(c.Kind), IsStaged = c.IsStaged };
            if (c.IsStaged) output.Staged.Add(rec); else output.Unstaged.Add(rec);
        }
        return Task.FromResult(output);
    }

    public sealed class RemoteOpOutput
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    [Description("将当前项目所有变更（含未跟踪文件）暂存并提交。message 为提交说明（首行，简明描述本次改动）。" +
                 "此操作不可逆——会创建新提交。需要用户审批确认。提交前建议先调用 git_status 查看变更。")]
    public Task<CommitOutput> GitCommitAsync(
        [Description("提交说明（commit message）。简明描述本次改动，如「修复登录页空引用异常」。")] string message,
        CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
            return Task.FromResult(new CommitOutput { Success = false, Error = "当前未打开 git 项目目录。" });
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult(new CommitOutput { Success = false, Error = "提交说明不能为空。" });

        var status = _git.GetStatus(dir);
        if (status.Count == 0)
            return Task.FromResult(new CommitOutput { Success = false, Error = "无变更可提交（工作区干净）。" });

        // 暂存所有未暂存变更（含未跟踪文件），再提交。
        foreach (var c in status)
            if (!c.IsStaged) _git.Stage(dir, c.Path);

        var ok = _git.Commit(dir, message.Trim());
        if (ok) _notifier?.NotifyChanged(); // 通知 SCM 页刷新（AI 后台提交后界面同步）。
        return Task.FromResult(new CommitOutput
        {
            Success = ok,
            Message = ok ? message.Trim() : null,
            FilesCommitted = ok ? status.Count : 0,
            Error = ok ? null : "提交失败（GitService.Commit 返回 false）。",
        });
    }

    [Description("推送当前分支的提交到远程仓库（如 GitHub）。不可逆——会公开本地提交到远程。" +
                 "需要用户审批确认。凭证（GitHub 用户名 + PAT）需先在源代码管理页「远程设置」配置。")]
    public Task<RemoteOpOutput> GitPushAsync(CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
            return Task.FromResult(new RemoteOpOutput { Success = false, Error = "当前未打开 git 项目目录。" });
        var (ok, msg) = _git.Push(dir);
        if (ok) _notifier?.NotifyChanged();
        return Task.FromResult(new RemoteOpOutput { Success = ok, Message = ok ? msg : null, Error = ok ? null : msg });
    }

    [Description("从远程仓库拉取并合并更新到当前分支。会修改本地工作区——若远程有更新则合并到本地。" +
                 "可能产生合并冲突（返回 Message 含冲突文件列表）。需要用户审批确认。凭证需先在「远程设置」配置。")]
    public Task<RemoteOpOutput> GitPullAsync(CancellationToken cancellationToken = default)
    {
        var dir = _projectDir.CurrentDirectory;
        if (string.IsNullOrEmpty(dir) || !_git.IsRepository(dir))
            return Task.FromResult(new RemoteOpOutput { Success = false, Error = "当前未打开 git 项目目录。" });
        var (ok, msg) = _git.Pull(dir);
        if (ok) _notifier?.NotifyChanged();
        return Task.FromResult(new RemoteOpOutput { Success = ok, Message = ok ? msg : null, Error = ok ? null : msg });
    }

    private static string Letter(ChangeKind kind) => kind switch
    {
        ChangeKind.Modified => "M",
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Untracked => "U",
        ChangeKind.Renamed => "R",
        _ => "?",
    };
}
