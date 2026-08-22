using System.ComponentModel;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>agent_configure</c> 工具：让 agent 配置「任意一个」自定义 agent（按名字定位），像页面「先选中 agent →
/// 改字段 → 保存」一样。可配置自己也可配置别人——「配置谁」优先看显式 <c>agent</c> 名字参数；名字缺省时
/// 用 ambient 上下文（<see cref="AgentExecutionContext.CurrentAgentId"/>）定位「正在调用本工具的当前 agent」，
/// 实现 agent 零参数自举（无须知道自己叫什么）。
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

    /// <summary>
    /// 解析目标 agent 定义：<paramref name="agent"/> 非空 → 按名字精确匹配；为空 → 用 ambient 上下文定位
    /// 「当前正在运行的 agent」并借其 Id 反查名字。返回 (定义, 用于报错的定位标签)。
    /// </summary>
    private async Task<(CustomAgentDefinition? Def, string Label)> ResolveTargetAsync(string? agent, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var def = (await _catalog.ListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(d => string.Equals(d.Name, agent, StringComparison.Ordinal));
            return (def, agent);
        }

        var currentIdText = AgentExecutionContext.CurrentAgentId.Value;
        if (!string.IsNullOrEmpty(currentIdText) && Guid.TryParse(currentIdText, out var currentId))
        {
            var current = await _catalog.GetAsync(currentId, ct).ConfigureAwait(false);
            if (current is not null)
                return (current, current.Name);
        }

        // ambient 上下文不可用（不在 agent run 内，或 workflow 路径无单一身份）——引导显式给名字。
        return (null, null!);
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
        [Description("目标 agent 的名字（精确匹配；可用 ListCustomAgents 查看现有名字）。留空则定位「正在调用本工具的当前 agent」。")] string? agent = null,
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
            // 定位目标：显式名字优先；缺省用 ambient 上下文定位「当前 agent」。
            var (def, label) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
            if (def is null)
            {
                var hint = string.IsNullOrWhiteSpace(agent)
                    ? "未提供名字且不在 agent 运行上下文内（workflow 路径或非 agent 调用）；请显式指定 agent 名字。"
                    : $"找不到名为「{agent}」的 agent。";
                return new ConfigureOutput { Success = false, Agent = label, Error = hint };
            }

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

    [Description("重新召唤一个自定义 agent（按名字定位；缺省定位当前 agent），使其缓存失效、下一轮 run 重建（带上最新配置）。" +
                 "通常在配置完工具后调用。注意：重新召唤会丢弃该 agent 会话的运行时记忆。需要用户审批确认。")]
    public async Task<ReloadOutput> ReloadAsync(
        [Description("目标 agent 的名字。留空则定位「正在调用本工具的当前 agent」。")] string? agent = null,
        CancellationToken cancellationToken = default)
    {
        var (resolved, label) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            var hint = string.IsNullOrWhiteSpace(agent)
                ? "未提供名字且不在 agent 运行上下文内；请显式指定 agent 名字。"
                : $"找不到名为「{agent}」的 agent。";
            return new ReloadOutput { Success = false, Error = hint };
        }

        _runner.Invalidate(resolved.Id);
        return new ReloadOutput { Success = true, AgentId = resolved.Id.ToString() };
    }
}
