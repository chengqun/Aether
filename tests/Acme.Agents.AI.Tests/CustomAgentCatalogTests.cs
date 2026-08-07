using Acme.Agents.AI;
using Acme.Notes.Application;
using Acme.Notes.Domain;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Infrastructure.Agents;
using Acme.Notes.Infrastructure.DependencyInjection;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Harness.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Agents.AI.Tests;

public class CustomAgentCatalogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _provider;

    public CustomAgentCatalogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agents-test-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddNotesInfrastructure(_dbPath);
        services.AddNotesApplication();
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Save_List_Get_Update_Delete_round_trip()
    {
        var notes = _provider.GetRequiredService<INoteService>();
        var catalog = new CustomAgentCatalog(notes, new ToolRegistry(), new SkillRegistry(), new DefaultModelSource(), new AgentSourceParser(), new RuleSourceParser(), new CodeSourceParser(), new RuleRegistry());

        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "测试助手",
            Description = "单元测试用",
            Category = "测试",
            SystemPrompt = "你是测试助手。",
            ModelId = "abc-123",
            Tools = new[] { "ReadFile", "Grep" },
            Memory = AgentMemoryMode.SlidingWindow,
            MaxHistory = 10,
        };

        // 新增
        await catalog.SaveAsync(def);

        // 列表能取到
        var list = await catalog.ListAsync();
        var saved = Assert.Single(list);
        Assert.Equal("测试助手", saved.Name);
        Assert.Equal("测试", saved.Category);
        Assert.Equal("你是测试助手。", saved.SystemPrompt);
        Assert.Equal("abc-123", saved.ModelId);
        Assert.Equal(new[] { "ReadFile", "Grep" }, saved.Tools);
        Assert.Equal(AgentMemoryMode.SlidingWindow, saved.Memory);
        Assert.Equal(10, saved.MaxHistory);

        // 注意：新增后 Note 的 id 由仓储分配，与传入 def.Id 不同；取实际 id
        var realId = saved.Id;

        // Get 单个
        var fetched = await catalog.GetAsync(realId);
        Assert.NotNull(fetched);
        Assert.Equal("测试助手", fetched!.Name);

        // 更新（已存在 → Update）
        fetched.Name = "测试助手2";
        fetched.SystemPrompt = "新提示词";
        await catalog.SaveAsync(fetched);

        var afterUpdate = await catalog.GetAsync(realId);
        Assert.NotNull(afterUpdate);
        Assert.Equal("测试助手2", afterUpdate!.Name);
        Assert.Equal("新提示词", afterUpdate.SystemPrompt);
        Assert.Single(await catalog.ListAsync()); // 仍只有一条

        // 删除
        await catalog.DeleteAsync(realId);
        Assert.Empty(await catalog.ListAsync());
    }

    [Fact]
    public async Task ListAsync_registers_rule_agents_into_rule_registry()
    {
        var notes = _provider.GetRequiredService<INoteService>();
        var ruleRegistry = new RuleRegistry();
        var catalog = new CustomAgentCatalog(notes, new ToolRegistry(), new SkillRegistry(), new DefaultModelSource(), new AgentSourceParser(), new RuleSourceParser(), new CodeSourceParser(), ruleRegistry);

        // 普通 agent + 规则 agent 各一
        await catalog.SaveAsync(new CustomAgentDefinition
        {
            Name = "普通助手",
            Description = "",
            Category = "普通对话",
            SystemPrompt = "你是助手。",
            NodeType = NoteNodeType.Agent,
        });
        await catalog.SaveAsync(new CustomAgentDefinition
        {
            Name = "查询规则",
            Description = "",
            Category = "规则",
            Engine = "Rule",
            NodeType = NoteNodeType.Rule,
            SystemPrompt = """
                           steps:
                             - match:
                                 onUserRegex: '查(\d+)'
                               action:
                                 tool: Echo
                                 args:
                                   text: '{user.1}'
                             - match:
                                 minToolCalls: 1
                               action:
                                 replyText: '结果：{result}'
                           noMatchReply: 无匹配规则
                           maxSteps: 20
                           """,
        });

        var list = await catalog.ListAsync();
        // ListAsync 含 Agent ‖ Rule，NodeType 正确
        Assert.Equal(2, list.Count);
        Assert.Contains(list, d => d.Name == "普通助手" && d.NodeType == NoteNodeType.Agent);
        var rule = Assert.Single(list, d => d.Name == "查询规则");
        Assert.Equal(NoteNodeType.Rule, rule.NodeType);
        Assert.Equal("Rule", rule.Engine);

        // 规则 agent 经 ListAsync 注册进 RuleRegistry（供 run_rule 按名分发）
        Assert.True(ruleRegistry.TryGet("查询规则", out var entry));
        Assert.NotNull(entry!.Program);
        Assert.Equal(2, entry.Program.Steps.Count);
        // 普通节点不注册
        Assert.False(ruleRegistry.TryGet("普通助手", out _));

        // catalog 对 Rule 用 RuleSourceParser：规则笔记是单个 YAML 文档（无 front-matter ---），
        // 而普通 agent 笔记仍是 front-matter+body（有 ---）。
        var allNotes = await notes.GetAllAsync();
        var ruleNote = Assert.Single(allNotes, n => n.NodeType == NoteNodeType.Rule);
        var agentNote = Assert.Single(allNotes, n => n.NodeType == NoteNodeType.Agent);
        Assert.DoesNotContain("---", ruleNote.Content);
        Assert.Contains("---", agentNote.Content);
    }

    [Fact]
    public void GetAvailableTools_maps_registry_metas()
    {
        var notes = _provider.GetRequiredService<INoteService>();
        var registry = new ToolRegistry();
        registry.Register("ReadFile", "读取文件", "系统工具", _ => null!);
        registry.Register("QueryAccount", "查询账户", "期货交易", _ => null!);

        var catalog = new CustomAgentCatalog(notes, registry, new SkillRegistry(), new DefaultModelSource(), new AgentSourceParser(), new RuleSourceParser(), new CodeSourceParser(), new RuleRegistry());
        var tools = catalog.GetAvailableTools();

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "ReadFile" && t.Category == "系统工具");
        Assert.Contains(tools, t => t.Name == "QueryAccount" && t.Category == "期货交易");
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
