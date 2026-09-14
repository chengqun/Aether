using Acme.Qifu.AI;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Acme.Qifu.AI.Tests;

public class QifuToolApprovalTests
{
    private static ToolRegistry BuildRegistry()
    {
        var builder = Host.CreateApplicationBuilder();
        var aether = builder.Services.AddAetherAI();
        aether.Configure(o => { o.OpenAI.ApiKey = "offline"; o.OpenAI.ModelId = "offline"; });
        aether.AddQifuTools();

        var app = builder.Build();
        return app.Services.GetRequiredService<ToolRegistry>();
    }

    [Fact]
    public void AsyncSqlQuery_is_Dangerous_so_it_cannot_auto_run()
    {
        // 回归守卫：AsyncSqlQuery 提交任意 SQL（可含 DROP/DELETE/UPDATE 不可逆语句）。
        // 旧实现未声明审批等级 → ToolRegistry 默认 Safe → 自动放行，无需任何审批。
        var registry = BuildRegistry();

        Assert.True(registry.Contains("AsyncSqlQuery"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("AsyncSqlQuery"));
    }

    [Fact]
    public void Read_only_qifu_tools_remain_Safe()
    {
        var registry = BuildRegistry();

        foreach (var name in new[]
                 {
                     "FetchSqlTaskStatus", "PollSqlTaskStatus", "DownloadResult",
                     "ReadResult", "SqlLog",
                 })
        {
            Assert.True(registry.Contains(name), $"missing tool: {name}");
            Assert.Equal(ToolApprovalLevel.Safe, registry.LevelOf(name));
        }
    }

    [Fact]
    public void Side_effect_qifu_tools_are_not_Safe()
    {
        var registry = BuildRegistry();

        // 权限工单（Dangerous）/ Teams 消息（Dangerous）/ 清理结果（Confirm）都不得为 Safe。
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("ApplyModelUsageAuthority"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("ApplyTableAuthority"));
        Assert.Equal(ToolApprovalLevel.Dangerous, registry.LevelOf("SendTeamsMessage"));
        Assert.Equal(ToolApprovalLevel.Confirm, registry.LevelOf("CleanResults"));
    }
}
