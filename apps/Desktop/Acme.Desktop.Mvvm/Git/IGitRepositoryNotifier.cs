namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// git 仓库变更通知（跨组件）。AI 工具（<c>GitTool</c> 的 git_commit/git_push/git_pull）在后台线程
/// 提交/同步后，经此通知 <see cref="SourceControlViewModel"/> 刷新——否则 SCM 页面不知道仓库已被改，
/// 变更列表/远程状态停留在旧值。平行于 catalog 变更通知（catalog Changed 驱动列表刷新）。
/// </summary>
public interface IGitRepositoryNotifier
{
    /// <summary>git 仓库被改动（提交/暂存/推送/拉取）时触发。可能在后台线程 raise。</summary>
    event Action? GitChanged;

    /// <summary>触发 <see cref="GitChanged"/>（AI 工具在写操作成功后调用）。</summary>
    void NotifyChanged();
}

/// <summary><see cref="IGitRepositoryNotifier"/> 默认实现（DI singleton）。</summary>
public sealed class GitRepositoryNotifier : IGitRepositoryNotifier
{
    public event Action? GitChanged;

    public void NotifyChanged() => GitChanged?.Invoke();
}
