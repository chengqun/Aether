using System.ComponentModel;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>agent_configure</c> 工具：让 agent 配置「任意一个」自定义 agent（按名字定位），像页面「先选中 agent →
/// 改字段 → 保存」一样。可配置自己也配置别人——「配置谁」由 <c>agent</c> 参数显式给定，不依赖运行时身份
/// （区别于「自动勾选当前 agent」那种脆弱的 AsyncLocal 定位）。
/// </summary>
/// <remarks>
/// 只暴露高频可配置项（tools/model/description/category/systemPrompt/requireToolApproval），其余字段
/// （采样参数、记忆模式、各 enableXxx 开关）暂不暴露，保留原值。需要更多字段时再扩展。
/// 与页面 <see cref="ExpertsViewModel.SaveAsync"/> 同构：读现定义 → 仅改传入字段 → SaveAsync → Invalidate 重建。
/// </remarks>
public sealed class AgentManagementTool
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;

    public AgentManagementTool(ICustomAgentCatalog catalog, ICustomAgentRunner runner)
    {
        _catalog = catalog;
        _runner = runner;
    }

    public sealed class ConfigureOutput
    {
        public bool Success { get; set; }
        public string? Agent { get; set; }
        public string? Error { get; set; }
    }

    public sealed class ReloadOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public string? Error { get; set; }
    }

    [Description("配置一个自定义 agent（按名字定位；可配置自己或任意 agent）。只改传入的字段，未传的保留原值。")]
    public async Task<ConfigureOutput> ConfigureAsync(
        [Description("目标 agent 的名字（精确匹配；可用 ListCustomAgents 查看现有名字）。")] string agent,
        [Description("可选：完整替换的勾选工具名数组（覆盖式）。传空数组表示清空工具。")] string[]? tools = null,
        [Description("可选：模型 id（空串表示用宿主默认模型；参见可用模型列表）。")] string? model = null,
        [Description("可选：描述。")] string? description = null,
        [Description("可选：分类。")] string? category = null,
        [Description("可选：系统提示词（完整替换）。")] string? systemPrompt = null,
        [Description("可选：是否开启工具审批（RequireToolApproval）。默认不改。")] bool? requireToolApproval = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 按名字定位（页面的「选一个 agent」）
            var all = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            var def = all.FirstOrDefault(d => string.Equals(d.Name, agent, StringComparison.Ordinal));
            if (def is null)
                return new ConfigureOutput { Success = false, Agent = agent, Error = $"找不到名为「{agent}」的 agent。" };

            // 仅改传入字段
            if (tools is not null) def.Tools = tools.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (model is not null) def.ModelId = model;
            if (description is not null) def.Description = description;
            if (category is not null) def.Category = category;
            if (systemPrompt is not null) def.SystemPrompt = systemPrompt;
            if (requireToolApproval is not null) def.RequireToolApproval = requireToolApproval.Value;

            await _catalog.SaveAsync(def, cancellationToken).ConfigureAwait(false);
            _runner.Invalidate(def.Id); // 若它正在被召唤/运行，重建使其生效

            return new ConfigureOutput { Success = true, Agent = def.Name };
        }
        catch (Exception ex)
        {
            return new ConfigureOutput { Success = false, Agent = agent, Error = $"配置失败：{ex.Message}" };
        }
    }

    [Description("重新召唤一个自定义 agent（按名字定位），使其缓存失效、下一轮 run 重建（带上最新配置）。" +
                 "通常在配置完工具后调用。注意：重新召唤会丢弃该 agent 会话的运行时记忆。需要用户审批确认。")]
    public async Task<ReloadOutput> ReloadAsync(
        [Description("目标 agent 的名字。")] string agent,
        CancellationToken cancellationToken = default)
    {
        var all = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        var resolved = all.FirstOrDefault(d => string.Equals(d.Name, agent, StringComparison.Ordinal));
        if (resolved is null)
            return new ReloadOutput { Success = false, Error = $"找不到名为「{agent}」的 agent。" };

        _runner.Invalidate(resolved.Id);
        return new ReloadOutput { Success = true, AgentId = resolved.Id.ToString() };
    }
}
