using System.ComponentModel;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;

namespace Acme.Mobile.Shared.Hosting.Tools;

/// <summary>
/// 手机端 agent 自举工具（<c>agent_configure</c>/<c>agent_reload</c>/<c>get_current_agent</c>）：
/// 镜像桌面 <c>AgentManagementTool</c>——复用 <see cref="ICustomAgentCatalog"/>（labs 目录版，已在
/// <c>AddMobileCommon</c> 注册）+ <see cref="ICustomAgentRunner"/> + <see cref="ToolRegistry"/>。
/// 与页面 <c>AgentEditViewModel.SaveAsync</c> 同构：读现定义 → 仅改传入字段 → SaveAsync → Invalidate 重建。
/// 缺省名字用 ambient 上下文（<see cref="AgentExecutionContext.CurrentAgentId"/>）定位「当前 agent」。
/// **省略 MCP 就绪检查**：手机端无 <see cref="Aether.AI.Abstractions.IMcpManagementService"/>，MissingTools 恒空
/// （配置存在的 MCP 工具在桌面端才会注册进 ToolRegistry）。
/// </summary>
public sealed class MobileAgentManagementTool
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly ToolRegistry? _toolRegistry;

    public MobileAgentManagementTool(ICustomAgentCatalog catalog, ICustomAgentRunner runner, ToolRegistry? toolRegistry = null)
    {
        _catalog = catalog;
        _runner = runner;
        _toolRegistry = toolRegistry;
    }

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

    public sealed class CurrentAgentOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? ModelId { get; set; }
        public string[]? Tools { get; set; }
        public bool? RequireToolApproval { get; set; }
        /// <summary>已勾选但未注册进 ToolRegistry 的工具名（MCP 工具在手机端恒空——连接在桌面端）。</summary>
        public string[]? MissingTools { get; set; }
        public string? Error { get; set; }
    }

    [Description("查询一个自定义 agent 的当前定义（缺省定位「正在调用本工具的当前 agent」）。只读，无副作用。")]
    public async Task<CurrentAgentOutput> GetCurrentAgentAsync(
        [Description("目标 agent 的名字。留空则定位「正在调用本工具的当前 agent」。")] string? agent = null,
        CancellationToken cancellationToken = default)
    {
        var (def, _) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
        if (def is null)
            return new CurrentAgentOutput
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(agent)
                    ? "不在 agent 运行上下文内；请显式指定 agent 名字。"
                    : $"找不到名为「{agent}」的 agent。",
            };
        return new CurrentAgentOutput
        {
            Success = true,
            AgentId = def.Id.ToString(),
            Name = def.Name,
            Description = def.Description,
            Category = def.Category,
            ModelId = def.ModelId,
            Tools = def.Tools.ToArray(),
            RequireToolApproval = def.RequireToolApproval,
            MissingTools = _toolRegistry is null ? Array.Empty<string>()
                : def.Tools.Where(n => !_toolRegistry.Contains(n)).ToArray(),
        };
    }

    [Description("配置一个自定义 agent（按名字定位；可配置自己或任意 agent）。只改传入的字段，未传的保留原值。")]
    public async Task<ConfigureOutput> ConfigureAsync(
        [Description("目标 agent 的名字（精确匹配）。留空则定位「正在调用本工具的当前 agent」。")] string? agent = null,
        [Description("可选：完整替换的勾选工具名数组（覆盖式）。传空数组表示清空工具。")] string[]? tools = null,
        [Description("可选：模型 id（空串表示用宿主默认模型）。")] string? model = null,
        [Description("可选：描述。")] string? description = null,
        [Description("可选：分类。")] string? category = null,
        [Description("可选：系统提示词（完整替换）。")] string? systemPrompt = null,
        [Description("可选：是否开启工具审批（RequireToolApproval）。默认不改。")] bool? requireToolApproval = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (def, label) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
            if (def is null)
            {
                var hint = string.IsNullOrWhiteSpace(agent)
                    ? "未提供名字且不在 agent 运行上下文内（workflow 路径或非 agent 调用）；请显式指定 agent 名字。"
                    : $"找不到名为「{agent}」的 agent。";
                return new ConfigureOutput { Success = false, Agent = label, Error = hint };
            }

            if (tools is not null) def.Tools = tools.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (model is not null) def.ModelId = model;
            if (description is not null) def.Description = description;
            if (category is not null) def.Category = category;
            if (systemPrompt is not null) def.SystemPrompt = systemPrompt;
            if (requireToolApproval is not null) def.RequireToolApproval = requireToolApproval.Value;

            await _catalog.SaveAsync(def, cancellationToken).ConfigureAwait(false);
            _runner.Invalidate(def.Id);

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
        var (def, _) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
        if (def is null)
            return new ReloadOutput
            {
                Success = false,
                Error = string.IsNullOrWhiteSpace(agent)
                    ? "未提供名字且不在 agent 运行上下文内；请显式指定 agent 名字。"
                    : $"找不到名为「{agent}」的 agent。",
            };

        _runner.Invalidate(def.Id);
        return new ReloadOutput { Success = true, AgentId = def.Id.ToString() };
    }
}
