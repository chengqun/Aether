using System;
using System.Linq;
using Acme.Desktop.Mvvm.Git;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="CommitGraphBuilder"/> 的 lane 分配算法测试。构造合成 <see cref="CommitNode"/> 拓扑
///（线性 / 分支+合并 / 三父 octopus / 空位复用 / 空），断言 lane 分配、segment 种类与连续性。</summary>
public class CommitGraphBuilderTests
{
    // 简易构造：sha 用单字母字符串，方便阅读。
    private static CommitNode Node(string sha, string[] parents, CommitRef[]? refs = null) =>
        new(new CommitInfo(sha, sha, sha, "a", DateTimeOffset.UnixEpoch), parents, refs ?? Array.Empty<CommitRef>());

    private static LaidOutCommit At(CommitGraphModel g, string sha) =>
        g.Commits.First(c => c.Info.Sha == sha);

    private static int CountElbowsBetween(CommitGraphModel g, int fromLane, int toLane) =>
        g.Segments.Count(s => s.Kind == SegmentKind.Elbow && s.FromLane == fromLane && s.ToLane == toLane);

    [Fact]
    public void Empty_NoCommitsNoSegments()
    {
        var g = CommitGraphBuilder.Build(Array.Empty<CommitNode>());
        Assert.Empty(g.Commits);
        Assert.Empty(g.Segments);
        Assert.Equal(0, g.LaneCount);
    }

    [Fact]
    public void Linear_AllOnSingleLane()
    {
        // newest first: C→B→A（A root）
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("C", new[] { "B" }),
            Node("B", new[] { "A" }),
            Node("A", Array.Empty<string>()),
        });

        Assert.Equal(1, g.LaneCount);
        Assert.Equal(0, At(g, "C").Lane);
        Assert.Equal(0, At(g, "B").Lane);
        Assert.Equal(0, At(g, "A").Lane);
        Assert.All(g.Segments, s => Assert.Equal(SegmentKind.Vertical, s.Kind));
        // C→B、B→A 两段竖线（A root 无出线）。
        Assert.Equal(2, g.Segments.Count);
        Assert.Equal(0, At(g, "C").Row);
        Assert.Equal(2, At(g, "A").Row);
    }

    [Fact]
    public void BranchAndMerge_TwoLanesWithElbows()
    {
        // M(merge C+D) → C→B→A ; D→B 。newest first: M,C,D,B,A
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("M", new[] { "C", "D" }),
            Node("C", new[] { "B" }),
            Node("D", new[] { "B" }),
            Node("B", new[] { "A" }),
            Node("A", Array.Empty<string>()),
        });

        Assert.Equal(2, g.LaneCount);
        Assert.Equal(0, At(g, "M").Lane); // merge 在 lane0
        Assert.Equal(0, At(g, "C").Lane); // 首父链续 lane0
        Assert.Equal(1, At(g, "D").Lane); // 次父开 lane1
        Assert.Equal(0, At(g, "B").Lane); // B 回 lane0（D 合并进）
        Assert.Equal(0, At(g, "A").Lane);

        // M 的次父 D：elbow 0→1（分支出）。
        Assert.True(CountElbowsBetween(g, 0, 1) >= 1);
        // D 的父 B 已被 C 预约在 lane0：合并 elbow 1→0。
        Assert.True(CountElbowsBetween(g, 1, 0) >= 1);
    }

    [Fact]
    public void OctopusMerge_ThreeParentsThreeLanes()
    {
        // M 合并 P1+P2+P3，三者皆子 of R。newest first: M,P1,P2,P3,R
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("M", new[] { "P1", "P2", "P3" }),
            Node("P1", new[] { "R" }),
            Node("P2", new[] { "R" }),
            Node("P3", new[] { "R" }),
            Node("R", Array.Empty<string>()),
        });

        Assert.Equal(3, g.LaneCount);
        Assert.Equal(0, At(g, "M").Lane);
        Assert.Equal(0, At(g, "P1").Lane); // 首父续 lane0
        Assert.Equal(1, At(g, "P2").Lane);
        Assert.Equal(2, At(g, "P3").Lane);
        Assert.Equal(0, At(g, "R").Lane); // 三路汇回 lane0
    }

    [Fact]
    public void FreeLaneReuse_FourChildrenOfR_OnlyTwoLanes()
    {
        // A,B,C,D 均为 R 的子提交（newest first A,B,C,D,R）。A 占 lane0 并预约 R；
        // B 开 lane1 再合并回 lane0（lane1 释放）；C、D 复用释放的 lane1。全程只用 2 lane。
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("A", new[] { "R" }),
            Node("B", new[] { "R" }),
            Node("C", new[] { "R" }),
            Node("D", new[] { "R" }),
            Node("R", Array.Empty<string>()),
        });

        Assert.Equal(2, g.LaneCount);                 // 不会开到 lane2
        Assert.Equal(0, At(g, "A").Lane);
        Assert.Equal(1, At(g, "B").Lane);
        Assert.Equal(1, At(g, "C").Lane);             // 复用 lane1
        Assert.Equal(1, At(g, "D").Lane);             // 复用 lane1
        Assert.Equal(0, At(g, "R").Lane);
    }

    [Fact]
    public void RowsAreTopological_NewestFirst()
    {
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("C", new[] { "B" }),
            Node("B", new[] { "A" }),
            Node("A", Array.Empty<string>()),
        });
        Assert.Equal(0, At(g, "C").Row);
        Assert.Equal(1, At(g, "B").Row);
        Assert.Equal(2, At(g, "A").Row);
    }

    [Fact]
    public void Continuity_EveryNonRootHasOutgoingSegment()
    {
        var g = CommitGraphBuilder.Build(new[]
        {
            Node("M", new[] { "C", "D" }),
            Node("C", new[] { "B" }),
            Node("D", new[] { "B" }),
            Node("B", new[] { "A" }),
            Node("A", Array.Empty<string>()),
        });
        // 每个非根 commit 至少有一段以其 row 为 FromRow 的出线（连向父）。
        foreach (var c in g.Commits)
        {
            if (c.Info.Sha == "A") continue; // root
            Assert.Contains(g.Segments, s => s.FromRow == c.Row);
        }
    }
}
