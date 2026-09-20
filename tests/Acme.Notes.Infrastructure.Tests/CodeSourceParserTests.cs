using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Infrastructure.Agents;

namespace Acme.Notes.Infrastructure.Tests;

/// <summary>
/// 代码规则 front-matter 的 <c>language:</c> 字段（F# 变体 .coderule.fs 的权威路由信号）解析/序列化测试。
/// </summary>
public class CodeSourceParserTests
{
    private const string FSharpSource = @"---
name: 查询规则
category: 代码规则
language: fsharp
---
type MyRule() =
    interface Aether.AI.Harness.FSharp.IFsRuleBrain with
        member _.Decide(ctx) = Aether.AI.Harness.FSharp.Reply ctx.UserText
";

    [Fact]
    public void Language_fsharp_parsed()
    {
        var ok = new CodeSourceParser().TryParse(FSharpSource, out var def, out var error);

        Assert.True(ok, error);
        Assert.NotNull(def);
        Assert.Equal("fsharp", def!.CodeLanguage);
        Assert.Equal("查询规则", def.Name);
        Assert.Contains("IFsRuleBrain", def.SystemPrompt);
        Assert.Equal(NoteNodeType.CodeRule, def.NodeType);
    }

    [Fact]
    public void Language_defaults_to_null_for_csharp()
    {
        var cs = @"---
name: 回显
category: 代码规则
---
public class Echo : Aether.AI.Harness.Agents.CodeRuleBrain { }
";
        var ok = new CodeSourceParser().TryParse(cs, out var def, out var error);

        Assert.True(ok, error);
        Assert.NotNull(def);
        Assert.Null(def!.CodeLanguage); // 缺省 = csharp（平台侧 null/空 语义同 csharp）
    }

    [Fact]
    public void Serialize_writes_language_only_for_fsharp()
    {
        var parser = new CodeSourceParser();
        var fsharp = parser.Serialize(new CustomAgentDefinition
        {
            Id = Guid.NewGuid(), Name = "n", NodeType = NoteNodeType.CodeRule,
            Engine = "Rule", CodeLanguage = "fsharp", SystemPrompt = "type X() = class end",
        });
        Assert.Contains("language: fsharp", fsharp);

        var csharp = parser.Serialize(new CustomAgentDefinition
        {
            Id = Guid.NewGuid(), Name = "n", NodeType = NoteNodeType.CodeRule,
            Engine = "Rule", SystemPrompt = "public class X { }",
        });
        Assert.DoesNotContain("language:", csharp); // 默认 csharp 不写出——保旧文件稳定
    }

    [Fact]
    public void Roundtrip_preserves_language()
    {
        var parser = new CodeSourceParser();
        var source = parser.Serialize(new CustomAgentDefinition
        {
            Id = Guid.NewGuid(), Name = "n", NodeType = NoteNodeType.CodeRule,
            Engine = "Rule", CodeLanguage = "fsharp", SystemPrompt = "type X() = class end",
        });

        Assert.True(parser.TryParse(source, out var def, out _));
        Assert.Equal("fsharp", def!.CodeLanguage);
    }
}
