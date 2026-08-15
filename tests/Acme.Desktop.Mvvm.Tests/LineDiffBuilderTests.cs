using System.Linq;
using Acme.Desktop.Mvvm.Git;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="LineDiffBuilder"/> 的 LCS 行对齐测试，以及 <see cref="AgentEditPreview"/> 的 new 内容计算测试。</summary>
public class LineDiffBuilderTests
{
    private static DiffLine? Line(IReadOnlyList<DiffLine> lines, int i) =>
        i < lines.Count ? lines[i] : null;

    [Fact]
    public void PureAdd_AllNewLinesAreAddedWithBlanksOnOldSide()
    {
        var model = LineDiffBuilder.Build("", "a\nb", "/p/f.txt", "csharp");

        Assert.False(model.IsBinary);
        Assert.All(model.NewLines, l => Assert.Equal(DiffLineKind.Added, l.Kind));
        Assert.All(model.OldLines, l => Assert.Equal(DiffLineKind.Blank, l.Kind));
        Assert.Equal(2, model.NewLines.Count);
    }

    [Fact]
    public void PureRemove_AllOldLinesAreRemovedWithBlanksOnNewSide()
    {
        var model = LineDiffBuilder.Build("a\nb", "", "/p/f.txt", null);

        Assert.All(model.OldLines, l => Assert.Equal(DiffLineKind.Removed, l.Kind));
        Assert.All(model.NewLines, l => Assert.Equal(DiffLineKind.Blank, l.Kind));
    }

    [Fact]
    public void MixedChange_ContextLinesAligned()
    {
        var oldText = "keep1\nchange\nkeep2";
        var newText = "keep1\nCHANGED\nkeep2";
        var model = LineDiffBuilder.Build(oldText, newText, "/p/f.txt", null);

        // LCS 对齐：context(keep1) + 一个 removed(change) + 一个 added(CHANGED) + context(keep2) = 4 行。
        // 相邻 delete/insert 的先后由回溯方向决定，两种顺序都是合法 LCS 对齐——按多重集断言，不锁行序。
        Assert.Equal(4, model.OldLines.Count);
        Assert.Equal(4, model.NewLines.Count);

        // 首尾 context 对齐（LCS 保证了公共行在两端）。
        Assert.Equal(DiffLineKind.Context, model.OldLines[0].Kind);
        Assert.Equal("keep1", model.NewLines[0].Text);
        Assert.Equal(DiffLineKind.Context, model.OldLines[3].Kind);
        Assert.Equal("keep2", model.NewLines[3].Text);

        // 中间两行：旧侧一个 Removed("change")+一个 Blank；新侧一个 Blank+一个 Added("CHANGED")。
        Assert.Contains(model.OldLines, l => l.Kind == DiffLineKind.Removed && l.Text == "change");
        Assert.Contains(model.OldLines, l => l.Kind == DiffLineKind.Blank);
        Assert.Contains(model.NewLines, l => l.Kind == DiffLineKind.Added && l.Text == "CHANGED");
        Assert.Contains(model.NewLines, l => l.Kind == DiffLineKind.Blank);
        Assert.Equal(2, model.OldLines.Count(l => l.Kind == DiffLineKind.Context));
        Assert.Equal(2, model.NewLines.Count(l => l.Kind == DiffLineKind.Context));
    }

    [Fact]
    public void InsertInMiddle_AlignsWithContext()
    {
        var oldText = "a\nc";
        var newText = "a\nb\nc";
        var model = LineDiffBuilder.Build(oldText, newText, "/p/f.txt", null);

        // a(context) / b(added, old侧Blank) / c(context)
        Assert.Equal("a", model.OldLines[0].Text);
        Assert.Equal(DiffLineKind.Context, model.OldLines[0].Kind);
        Assert.Equal(DiffLineKind.Blank, model.OldLines[1].Kind);
        Assert.Equal(DiffLineKind.Added, model.NewLines[1].Kind);
        Assert.Equal("b", model.NewLines[1].Text);
        Assert.Equal(DiffLineKind.Context, model.OldLines[2].Kind);
    }

    [Fact]
    public void Binary_WhenNullByte_IsBinary()
    {
        var model = LineDiffBuilder.Build("a\0b", "a\nb", "/p/f.bin", null);
        Assert.True(model.IsBinary);
    }

    [Fact]
    public void BothEmpty_ReturnsEmptyNonBinary()
    {
        var model = LineDiffBuilder.Build("", "", "/p/f.txt", null);
        Assert.False(model.IsBinary);
        Assert.Empty(model.OldLines);
        Assert.Empty(model.NewLines);
    }

    [Fact]
    public void LargeFile_DegradesToNaiveAlign_BothSidesEqualLength()
    {
        // 超过 LcsCellBudget(5M) 格点 → 退化。1000 x 6000 = 6M。
        var oldText = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"old{i}"));
        var newText = string.Join('\n', Enumerable.Range(0, 6000).Select(i => $"new{i}"));
        var model = LineDiffBuilder.Build(oldText, newText, "/p/big.txt", null);

        Assert.False(model.IsBinary);
        // 退化时补 Blank 对齐 → 两侧等长 = max(1000,6000) = 6000。
        Assert.Equal(model.OldLines.Count, model.NewLines.Count);
        Assert.Equal(6000, model.NewLines.Count);
        Assert.All(model.OldLines, l => Assert.True(l.Kind is DiffLineKind.Removed or DiffLineKind.Blank));
    }
}
