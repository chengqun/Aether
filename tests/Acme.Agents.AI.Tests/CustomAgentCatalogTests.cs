using Acme.Agents.AI;
using Acme.Notes.Application;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.AgentSource;
using Acme.Notes.Infrastructure.DependencyInjection;
using Aether.AI.Harness.Tools;
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
        var catalog = new CustomAgentCatalog(notes, new ToolRegistry());

        var def = new CustomAgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "测试助手",
            Description = "单元测试用",
            Category = "测试",
            SystemPrompt = "你是测试助手。",
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
    public void GetAvailableTools_maps_registry_metas()
    {
        var notes = _provider.GetRequiredService<INoteService>();
        var registry = new ToolRegistry();
        registry.Register("ReadFile", "读取文件", "系统工具", _ => null!);
        registry.Register("QueryAccount", "查询账户", "期货交易", _ => null!);

        var catalog = new CustomAgentCatalog(notes, registry);
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
