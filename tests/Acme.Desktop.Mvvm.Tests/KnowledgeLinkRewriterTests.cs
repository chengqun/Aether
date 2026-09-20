using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// 知识对象引用改写的规范写法测试（<see cref="KnowledgeLinkRewriter.CanonicalTargetId"/>）：
/// 改名/迁移/合并后改写入边 targetId 用的唯一权威——同目录裸名（目录迁移不断链）、
/// 跨目录 labs 根相对带 knowledge/ 前缀（不含扩展名）。两者都须是 OkTargetResolver 一级可解析候选。
/// </summary>
public class KnowledgeLinkRewriterTests
{
    private const string Labs = "/labs";
    private const string Knowledge = "/labs/knowledge";

    [Fact]
    public void Same_directory_uses_bare_stem()
    {
        // 同目录互链 → 裸名 stem（目录整体迁移不断链）
        var r = KnowledgeLinkRewriter.CanonicalTargetId(
            $"{Knowledge}/企架/2_Feature/源.md", $"{Knowledge}/企架/2_Feature/同设备识别.md", Labs);
        Assert.Equal("同设备识别", r);
    }

    [Fact]
    public void Cross_directory_uses_labs_relative_with_knowledge_prefix()
    {
        var r = KnowledgeLinkRewriter.CanonicalTargetId(
            $"{Knowledge}/企架/3_Method/源.md", $"{Knowledge}/企架/2_Feature/同设备识别.md", Labs);
        Assert.Equal("knowledge/企架/2_Feature/同设备识别", r);
    }

    [Fact]
    public void Canonical_form_is_resolvable_by_candidate_paths()
    {
        // 改写产物必须是 OkTargetResolver 一级候选（改完不断链）——裸名与 knowledge/ 前缀根相对两种都验证
        var bare = KnowledgeLinkRewriter.CanonicalTargetId(
            $"{Knowledge}/企架/2_Feature/源.md", $"{Knowledge}/企架/2_Feature/同设备识别.md", Labs);
        var cross = KnowledgeLinkRewriter.CanonicalTargetId(
            $"{Knowledge}/企架/3_Method/源.md", $"{Knowledge}/企架/2_Feature/同设备识别.md", Labs);

        Assert.Contains(OkTargetResolver.CandidateOkPaths(bare, Labs, $"{Knowledge}/企架/2_Feature"),
            p => p.EndsWith("同设备识别.md"));
        Assert.Contains(OkTargetResolver.CandidateOkPaths(cross, Labs, $"{Knowledge}/企架/3_Method"),
            p => p == $"{Labs}/knowledge/企架/2_Feature/同设备识别.md");
    }

    [Fact]
    public void Extension_is_stripped()
    {
        // targetId 约定不含 .md（解析侧自动补）
        var r = KnowledgeLinkRewriter.CanonicalTargetId(
            $"{Knowledge}/a/源.md", $"{Knowledge}/b/目标.md", Labs);
        Assert.DoesNotContain(".md", r);
    }
}
