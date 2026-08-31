using System;
using System.Collections.Generic;
using System.Linq;
using Acme.Notes.Domain.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary><see cref="KnowledgeExpansionBuilder"/> 递归展开逻辑测试。注入 fake <paramref name="resolve"/>
/// （不读盘），验证两种模式（作者标志 / 覆盖 depth）、多层递归、深度上限、去重防环、找不到文件、正文截断。</summary>
public class KnowledgeExpansionBuilderTests
{
    private const string FocalPath = "/root/A.md";

    private static KnowledgeObjectDefinition Def(string kind, string body, params (string role, string target, bool expand)[] links) => new()
    {
        Kind = kind,
        Body = body,
        Links = links.Select(l => new KnowledgeLink { Role = l.role, Target = l.target, Expand = l.expand }).ToList(),
    };

    /// <summary>建 fake resolve：map 里有 → (stem, 合成全路径, def)；没有 → (stem, 合成全路径, null)=找不到文件。</summary>
    private static Func<string?, string?, ResolvedKnowledgeTarget?> Resolve(Dictionary<string, KnowledgeObjectDefinition> map)
        => (t, _) =>
        {
            if (string.IsNullOrWhiteSpace(t)) return null;
            var stem = OkTargetResolver.StemOf(t);
            return map.TryGetValue(t, out var d)
                ? new ResolvedKnowledgeTarget(stem, $"/root/{t}.md", d)
                : new ResolvedKnowledgeTarget(stem, $"/root/{t}.md", null);
        };

    // —— 默认作者标志模式 ——

    [Fact]
    public void AuthorMode_NoExpandFlags_OnlyFocalInlined()
    {
        // 既有文件无 expand 字段 → 全 false → 默认只返回焦点本身（body + links 清单，无子节点内容）
        var a = Def("流程", "bodyA", ("uses", "B", false), ("then", "C", false));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = Def("数据源", "bodyB"), ["C"] = Def("规则", "bodyC") };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("path: A", outp);
        Assert.Contains("bodyA", outp);
        Assert.Contains("[uses] B (path:B)", outp);   // 列出
        Assert.Contains("[then] C (path:C)", outp);   // 列出
        Assert.DoesNotContain("bodyB", outp);          // 未内联
        Assert.DoesNotContain("bodyC", outp);
        Assert.DoesNotContain("[expand]", outp);       // 无标记
    }

    [Fact]
    public void AuthorMode_ExpandTrue_InlinesTargetOthersJustListed()
    {
        var a = Def("流程", "bodyA", ("uses", "B", true), ("then", "C", false));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = Def("数据源", "bodyB"), ["C"] = Def("规则", "bodyC") };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("bodyB", outp);   // B 内联
        Assert.DoesNotContain("bodyC", outp); // C 未内联
        Assert.Contains("[uses] B (path:B) [expand]", outp);  // 标记 [expand]
        Assert.DoesNotContain("[then] C (path:C) [expand]", outp);
    }

    [Fact]
    public void AuthorMode_ExpandChain_RecursesMultipleLevels()
    {
        // A expand→ B expand→ C（多层递归沿标过的链）
        var a = Def("流程", "bodyA", ("uses", "B", true));
        var b = Def("数据源", "bodyB", ("then", "C", true));
        var c = Def("规则", "bodyC");
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = b, ["C"] = c };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("bodyB", outp);  // 第 1 层
        Assert.Contains("bodyC", outp);  // 第 2 层（沿 B 的 expand 链递归）
    }

    // —— 覆盖模式 expandDepth ——

    [Fact]
    public void BlanketMode_Depth1_InlinesAllImmediateChildrenRegardlessOfFlag()
    {
        var a = Def("流程", "bodyA", ("uses", "B", false), ("then", "C", false));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = Def("数据源", "bodyB"), ["C"] = Def("规则", "bodyC") };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), 1);

        Assert.Contains("bodyB", outp);  // blanket 内联全部直接子节点（不论 expand）
        Assert.Contains("bodyC", outp);
    }

    [Fact]
    public void BlanketMode_Depth1_DoesNotRecurseIntoGrandchildren()
    {
        // N=1：内联直接子节点，但不再跟孙节点（孙节点只列在子节点的 links 清单）
        var a = Def("流程", "bodyA", ("uses", "B", false));
        var b = Def("数据源", "bodyB", ("then", "C", false));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = b, ["C"] = Def("规则", "bodyC") };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), 1);

        Assert.Contains("bodyB", outp);   // 子内联
        Assert.DoesNotContain("bodyC", outp); // 孙不内联
    }

    [Fact]
    public void BlanketMode_Depth0_OnlyFocal()
    {
        var a = Def("流程", "bodyA", ("uses", "B", false));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = Def("数据源", "bodyB") };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), 0);

        Assert.Contains("bodyA", outp);
        Assert.DoesNotContain("bodyB", outp);
    }

    // —— 安全阀 ——

    [Fact]
    public void AuthorMode_LongExpandChain_StopsAtDepthCap()
    {
        // A→B→C→D→E→F 全 expand=true，budget=MaxExpandDepth=4 → 内联 B,C,D,E（4 层），F 截到深度上限
        var a = Def("p", "bodyA", ("u", "B", true));
        var b = Def("p", "bodyB", ("u", "C", true));
        var c = Def("p", "bodyC", ("u", "D", true));
        var d = Def("p", "bodyD", ("u", "E", true));
        var e = Def("p", "bodyE", ("u", "F", true));
        var f = Def("p", "bodyF");
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = b, ["C"] = c, ["D"] = d, ["E"] = e, ["F"] = f };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("bodyE", outp);   // 第 4 层内联
        Assert.DoesNotContain("bodyF", outp); // 第 5 层截断
        Assert.Contains("已达展开深度上限", outp);
    }

    [Fact]
    public void AuthorMode_Cycle_SkipsAlreadyOnPath()
    {
        // A expand→ B expand→ A（环）：第二次遇 A 跳过防环
        var a = Def("流程", "bodyA", ("uses", "B", true));
        var b = Def("数据源", "bodyB", ("then", "A", true));
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = b }; // A 不在 map → 用传入的 a

        // 注意：A 在 focal 传入，resolve 对 "A" 返回 null def——但环检测只看 FullPath 是否在 visited。
        // focalFullPath=FocalPath="/root/A.md" 已种子进 visited，B 的 link "A" resolve 出 fullpath="/root/A.md" → 命中。
        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("bodyB", outp);       // B 内联一次
        Assert.Contains("已在展开路径上", outp); // B 回链 A 被跳过
    }

    [Fact]
    public void AuthorMode_TargetNotFound_NotesMissingFile()
    {
        var a = Def("流程", "bodyA", ("uses", "B", true));
        var map = new Dictionary<string, KnowledgeObjectDefinition>(); // B 不存在

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("找不到知识对象文件", outp);
    }

    [Fact]
    public void AuthorMode_OversizedBody_Truncated()
    {
        var big = new string('x', KnowledgeExpansionBuilder.MaxTargetInlineChars + 100);
        var a = Def("流程", "bodyA", ("uses", "B", true));
        var b = Def("数据源", big);
        var map = new Dictionary<string, KnowledgeObjectDefinition> { ["B"] = b };

        var outp = KnowledgeExpansionBuilder.BuildExpandedDefinition(a, "A", FocalPath, Resolve(map), null);

        Assert.Contains("已截断", outp);
        Assert.Contains($"原文共 {big.Length} 字符", outp);
    }
}
