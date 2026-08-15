using System;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>文件变更类型（映射 git status 状态字母）。</summary>
public enum ChangeKind
{
    Modified,   // M
    Added,      // A（已暂存的新增）
    Deleted,    // D
    Untracked,  // ?（工作区未跟踪）
    Renamed,    // R
}

/// <summary>单个文件的一条 git 状态记录。</summary>
/// <param name="Path">仓库内相对路径。</param>
/// <param name="Kind">变更类型。</param>
/// <param name="IsStaged">true=已暂存（index），false=工作区未暂存。</param>
public sealed record GitChange(string Path, ChangeKind Kind, bool IsStaged);

/// <summary>diff 行类型。</summary>
public enum DiffLineKind
{
    Context,  // 两侧都有（未变）
    Added,    // 仅右侧（新增）
    Removed,  // 仅左侧（删除）
    Blank,    // 对侧有增删时本侧的占位空行（对齐用）
}

/// <summary>diff 单行：文本 + 类型。</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind);

/// <summary>一个文件的左右并排 diff 模型。
/// <see cref="OldLines"/> 喂左编辑器（HEAD 旧版），<see cref="NewLines"/> 喂右编辑器（工作区新版）。
/// 两数组按行对齐：context 行两边都有同文；removed 行左有右补 Blank；added 行右有左补 Blank。</summary>
public sealed record DiffModel(
    string OldText,
    string NewText,
    IReadOnlyList<DiffLine> OldLines,
    IReadOnlyList<DiffLine> NewLines,
    bool IsBinary)
{
    public string? SyntaxKey { get; init; }
    public string? FilePath { get; init; }
}

// ── 提交历史拓扑图模型 ──

/// <summary>提交元信息。</summary>
/// <param name="Sha">完整 sha。</param>
/// <param name="ShaShort">短 sha（前 7 位）。</param>
/// <param name="Message">提交消息（首行）。</param>
/// <param name="Author">作者名。</param>
/// <param name="When">提交时间。</param>
public sealed record CommitInfo(string Sha, string ShaShort, string Message, string Author, DateTimeOffset When);

/// <summary>指向某 commit 的引用（分支/标签）。用于图上徽标。</summary>
public sealed record CommitRef(string Name, bool IsHead, bool IsRemote, bool IsTag);

/// <summary>一个 commit 的原始拓扑数据（喂 <see cref="CommitGraphBuilder"/>）。</summary>
/// <param name="Info">元信息。</param>
/// <param name="Parents">父 commit sha 列表（首父在前）。</param>
/// <param name="Refs">指向此 commit 的分支/标签。</param>
public sealed record CommitNode(CommitInfo Info, IReadOnlyList<string> Parents, IReadOnlyList<CommitRef> Refs);

/// <summary>一个 commit 在某次提交中的改动文件。</summary>
public sealed record CommitFileChange(string Path, ChangeKind Kind)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string StatusLetter => Kind switch
    {
        ChangeKind.Modified => "M",
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Untracked => "U",
        ChangeKind.Renamed => "R",
        _ => "?",
    };
}

/// <summary>已布局的 commit（带 lane 列号 + row 行号）。</summary>
public sealed record LaidOutCommit(CommitInfo Info, int Lane, int Row, IReadOnlyList<CommitRef> Refs);

/// <summary>图连线段类型。</summary>
public enum SegmentKind
{
    /// <summary>同 lane 的竖直连线（首父续本 lane / 穿过行）。</summary>
    Vertical,
    /// <summary>跨 lane 的肘形连线（分支/合并）。</summary>
    Elbow,
}

/// <summary>图上一段连线。颜色以 ARGB uint 携带（Mvvm 零 Avalonia，渲染侧转 Color）。</summary>
public sealed record GraphSegment(SegmentKind Kind, int FromLane, int ToLane, int FromRow, int ToRow, uint Color);

/// <summary>整张提交历史拓扑图（节点 + 连线 + lane 总数）。</summary>
public sealed record CommitGraphModel(IReadOnlyList<LaidOutCommit> Commits, IReadOnlyList<GraphSegment> Segments, int LaneCount);

// ── 远程同步 ──

/// <summary>当前分支相对其上游的领先/落后状态（本地计算，不联网；需先 fetch 才准确）。</summary>
/// <param name="UpstreamBranch">上游分支规范名（如 refs/remotes/origin/main）；无上游时为 null。</param>
/// <param name="Ahead">本地领先上游的提交数。</param>
/// <param name="Behind">本地落后上游的提交数。</param>
/// <param name="HasUpstream">当前分支是否已设置上游跟踪。</param>
/// <param name="RemoteUrl">远程仓库 URL（如 https://github.com/...）；无远程时为 null。</param>
public sealed record RemoteStatus(string? UpstreamBranch, int Ahead, int Behind, bool HasUpstream, string? RemoteUrl);
