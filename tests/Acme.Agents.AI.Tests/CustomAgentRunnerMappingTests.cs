using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
using NotesAgentMemoryMode = Acme.Notes.Domain.Agents.AgentMemoryMode;

namespace Acme.Agents.AI.Tests;

/// <summary>
/// <see cref="CustomAgentRunner.ToAgentDefinition"/>：Notes 层 <see cref="CustomAgentDefinition"/>
/// → 平台 <see cref="AgentDefinition"/> 的字段映射。验证新增字段 <c>AutoApprovalRules</c> 透传，
/// 以及 <c>Summarize</c> 记忆模式经 <c>MapMemory</c> 映射到平台枚举。
/// </summary>
public class CustomAgentRunnerMappingTests
{
    [Fact]
    public void AutoApprovalRules_flow_through_to_platform_definition()
    {
        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "审批助手",
            SystemPrompt = "你是助手",
            RequireToolApproval = true,
            AutoApprovalRules = new List<string> { "ReadFile", "QueryStock" },
        };

        var platform = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);

        Assert.NotNull(platform.AutoApprovalRules);
        Assert.Equal(new[] { "ReadFile", "QueryStock" }, platform.AutoApprovalRules);
    }

    [Fact]
    public void Empty_auto_approval_rules_maps_to_empty_list()
    {
        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "普通助手",
            SystemPrompt = "你是助手",
            AutoApprovalRules = new List<string>(),
        };

        var platform = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);

        // Notes 层 AutoApprovalRules 是 List<string>（默认 new()），映射后平台层为非空空列表
        Assert.NotNull(platform.AutoApprovalRules);
        Assert.Empty(platform.AutoApprovalRules);
    }

    [Fact]
    public void Summarize_memory_maps_to_platform_summarize()
    {
        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "摘要助手",
            SystemPrompt = "你是助手",
            Memory = NotesAgentMemoryMode.Summarize,
            MaxHistory = 12,
        };

        var platform = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);

        Assert.Equal(Aether.AI.Core.Agents.AgentMemoryMode.Summarize, platform.Memory);
        Assert.Equal(12, platform.MaxHistory);
        // LLM agent：Engine 不解析成 Rule，Instructions 取 SystemPrompt
        Assert.Equal(EngineKind.Llm, platform.Engine);
        Assert.Equal("你是助手", platform.Instructions);
    }

    [Fact]
    public void BackgroundAgents_flow_through_to_platform_definition()
    {
        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "编排者",
            SystemPrompt = "你是编排者",
            BackgroundAgents = new List<string> { "分析助手", "交易助手" },
        };

        var platform = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);

        Assert.NotNull(platform.BackgroundAgents);
        Assert.Equal(new[] { "分析助手", "交易助手" }, platform.BackgroundAgents);
    }

    [Fact]
    public void Empty_background_agents_maps_to_empty_list()
    {
        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "单 agent",
            SystemPrompt = "你是助手",
            BackgroundAgents = new List<string>(),
        };

        var platform = CustomAgentRunner.ToAgentDefinition(def.Id.ToString(), def);

        Assert.NotNull(platform.BackgroundAgents);
        Assert.Empty(platform.BackgroundAgents);
    }
}
