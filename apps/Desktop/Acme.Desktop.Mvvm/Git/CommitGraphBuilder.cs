using System;
using System.Collections.Generic;

namespace Acme.Desktop.Mvvm.Git;

/// <summary>提交历史拓扑图的 lane 分配（纯函数，零 Avalonia 零 LibGit2Sharp，易测）。
/// 输入拓扑序（newest 在前）的 <see cref="CommitNode"/> 列表，产出 <see cref="CommitGraphModel"/>：
/// 每个 commit 分配 lane 列 + row 行，并发射 <see cref="GraphSegment"/> 连线
///（<see cref="SegmentKind.Vertical"/> 同列竖线 / <see cref="SegmentKind.Elbow"/> 跨列肘形）。
/// 镜像 <c>WorkflowGraphBuilder</c> 的模型→布局两段式。</summary>
public static class CommitGraphBuilder
{
    // 8 色调色板（ARGB，明暗通用对比度）：蓝/绿/紫/橙/青/红/黄绿/棕。
    private static readonly uint[] Palette =
    {
        0xFF42A5F5, 0xFF66BB6A, 0xFFAB47BC, 0xFFFFA726,
        0xFF26C6DA, 0xFFEF5350, 0xFFD4E157, 0xFF8D6E63,
    };

    public static CommitGraphModel Build(IReadOnlyList<CommitNode> nodes)
    {
        var commits = new List<LaidOutCommit>(nodes.Count);
        var segments = new List<GraphSegment>();
        var lanes = new List<Lane>();   // 活动车道；Sha=预约的下一个 commit，null=空位
        var laneCount = 0;              // 历史最大车道数（决定图宽）

        for (var r = 0; r < nodes.Count; r++)
        {
            var node = nodes[r];
            var sha = node.Info.Sha;

            // 1. 找 C 的 lane：被某子预约的位；否则空位；否则新增。
            var laneIdx = IndexOfReserved(lanes, sha);
            if (laneIdx < 0) laneIdx = IndexOfFree(lanes);
            if (laneIdx < 0)
            {
                laneIdx = lanes.Count;
                lanes.Add(new Lane(null, Palette[laneIdx % Palette.Length]));
            }
            // 消费 C 的预约（C 已在此位）。
            lanes[laneIdx] = lanes[laneIdx] with { Sha = null };

            commits.Add(new LaidOutCommit(node.Info, laneIdx, r, node.Refs));
            laneCount = Math.Max(laneCount, laneIdx + 1);

            // 2. pass-through：其它仍预约的 lane 画穿过 r 的竖线（r→r+1）。
            for (var i = 0; i < lanes.Count; i++)
            {
                if (i == laneIdx) continue;
                if (lanes[i].Sha is not null)
                    segments.Add(new GraphSegment(SegmentKind.Vertical, i, i, r, r + 1, lanes[i].Color));
            }

            // 3. 父提交：首父续本 lane（竖线）；其余父开/接到 lane（肘形）；父已被别子预约=合并肘。
            var parents = node.Parents;
            var first = true;
            for (var pi = 0; pi < parents.Count; pi++)
            {
                var p = parents[pi];
                var ex = IndexOfReserved(lanes, p);
                if (ex >= 0 && ex != laneIdx)
                {
                    // 父已被别子预约 → 合并肘（C.lane → ex.lane），父留 ex 预约不变。
                    segments.Add(new GraphSegment(SegmentKind.Elbow, laneIdx, ex, r, r + 1, lanes[ex].Color));
                }
                else if (ex == laneIdx)
                {
                    // 父已预约在本 lane（重复父，罕见）→ 直行，保留预约。
                    if (first)
                        segments.Add(new GraphSegment(SegmentKind.Vertical, laneIdx, laneIdx, r, r + 1, lanes[laneIdx].Color));
                }
                else if (first)
                {
                    // 首父续本 lane（直下）。
                    lanes[laneIdx] = lanes[laneIdx] with { Sha = p };
                    segments.Add(new GraphSegment(SegmentKind.Vertical, laneIdx, laneIdx, r, r + 1, lanes[laneIdx].Color));
                }
                else
                {
                    // 其余父：开新/空 lane 接入（分支肘）。
                    var free = IndexOfFree(lanes);
                    if (free < 0)
                    {
                        free = lanes.Count;
                        lanes.Add(new Lane(null, Palette[free % Palette.Length]));
                    }
                    lanes[free] = lanes[free] with { Sha = p };
                    segments.Add(new GraphSegment(SegmentKind.Elbow, laneIdx, free, r, r + 1, lanes[free].Color));
                }
                first = false;
            }
            // root 提交（无父）：本 lane 不再预约，自然终止。
        }

        return new CommitGraphModel(commits, segments, laneCount);
    }

    private static int IndexOfReserved(List<Lane> lanes, string sha)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i].Sha is not null && string.Equals(lanes[i].Sha, sha, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int IndexOfFree(List<Lane> lanes)
    {
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i].Sha is null) return i;
        return -1;
    }

    private sealed record Lane(string? Sha, uint Color);
}
