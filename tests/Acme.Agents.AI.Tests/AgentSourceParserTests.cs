using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Infrastructure.Agents;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="AgentSourceParser"/>：YAML front-matter + Markdown body ↔ <see cref="CustomAgentDefinition"/>。
/// 验证新增字段 <c>auto_approval_rules</c>（自动放行工具名列表）的 flow/block 往返，
/// 以及 <c>memory: Summarize</c> 枚举按名 round-trip。
/// </summary>
public class AgentSourceParserTests
{
    private static readonly AgentSourceParser _parser = new();

    [Fact]
    public void AutoApprovalRules_flow_round_trips()
    {
        var def = new CustomAgentDefinition
        {
            Name = "审批助手",
            SystemPrompt = "你是助手",
            RequireToolApproval = true,
            AutoApprovalRules = new List<string> { "ReadFile", "QueryStock" },
        };

        var source = _parser.Serialize(def);
        Assert.Contains("auto_approval_rules: [\"ReadFile\",\"QueryStock\"]", source);

        Assert.True(_parser.TryParse(source, out var parsed, out var error));
        Assert.Empty(error); // 成功时 error 为空串
        Assert.Equal(new[] { "ReadFile", "QueryStock" }, parsed!.AutoApprovalRules);
    }

    [Fact]
    public void AutoApprovalRules_empty_omitted_from_source()
    {
        var def = new CustomAgentDefinition
        {
            Name = "无审批助手",
            SystemPrompt = "你是助手",
            AutoApprovalRules = new List<string>(),
        };

        var source = _parser.Serialize(def);
        Assert.DoesNotContain("auto_approval_rules", source);

        Assert.True(_parser.TryParse(source, out var parsed, out _));
        Assert.Empty(parsed!.AutoApprovalRules);
    }

    [Fact]
    public void AutoApprovalRules_block_style_parsed()
    {
        const string source = """
                              ---
                              name: 块样式助手
                              auto_approval_rules:
                                - ReadFile
                                - QueryStock
                              ---
                              你是助手
                              """;
        Assert.True(_parser.TryParse(source, out var parsed, out _));
        Assert.Equal(new[] { "ReadFile", "QueryStock" }, parsed!.AutoApprovalRules);
    }

    [Fact]
    public void Memory_summarize_round_trips_by_name()
    {
        var def = new CustomAgentDefinition
        {
            Name = "摘要助手",
            SystemPrompt = "你是助手",
            Memory = AgentMemoryMode.Summarize,
            MaxHistory = 8,
        };

        var source = _parser.Serialize(def);
        Assert.Contains("memory: Summarize", source);

        Assert.True(_parser.TryParse(source, out var parsed, out _));
        Assert.Equal(AgentMemoryMode.Summarize, parsed!.Memory);
        Assert.Equal(8, parsed.MaxHistory);
    }
}
