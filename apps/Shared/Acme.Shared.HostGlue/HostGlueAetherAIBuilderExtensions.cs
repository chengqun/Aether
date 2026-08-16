using Aether.AI.Core.Agents;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Shared.HostGlue;

/// <summary>
/// 宿主胶水 DI 扩展（UI 无关，桌面/手机共用）。
/// </summary>
public static class HostGlueAetherAIBuilderExtensions
{
    /// <summary>
    /// 注册规则程序注册表 + <c>run_rule</c> 工具。规则 agent（Engine=Rule）的程序同时可被 LLM agent 经
    /// <c>run_rule(name,input)</c> 调用（省 token：executor 内部跑工具，原始结果对 LLM 不可见）。
    /// <see cref="RuleRegistry"/> 由 <c>CustomAgentCatalog</c> 在 ListAsync 时把每个 Engine=="Rule" 的 agent
    /// 按名注册其规则程序；run_rule 按名查表。标 Safe（规则是作者显式写的确定性流程）。
    /// 合并自原桌面与手机的 run_rule 注册块（逐字相同）。
    /// </summary>
    public static AetherAIBuilder AddRunRuleTool(this AetherAIBuilder aether)
    {
        aether.Services.AddSingleton<RuleRegistry>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            RuleTool.ToolName, "运行一个已注册的规则程序（确定性，无 LLM，适合机械的数据查询/工具编排）。参数 name=规则名,input=查询。", "规则",
            sp => RuleTool.Create(
                sp.GetRequiredService<RuleRegistry>(),
                name => sp.GetRequiredService<ToolRegistry>().ResolveAll(new[] { name }).FirstOrDefault()),
            ToolApprovalLevel.Safe));
        return aether;
    }
}
