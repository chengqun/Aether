using System.ComponentModel;
using Acme.Notes.Domain.Agents;
using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Tools;

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
    private readonly IMcpManagementService? _mgmt;
    private readonly ToolRegistry? _toolRegistry;

    public AgentManagementTool(ICustomAgentCatalog catalog, ICustomAgentRunner runner,
        IMcpManagementService? mgmt = null, ToolRegistry? toolRegistry = null)
    {
        _catalog = catalog;
        _runner = runner;
        _mgmt = mgmt;
        _toolRegistry = toolRegistry;
    }

    /// <summary>
    /// 确保 MCP 就绪（连接 + 工具注册进 <see cref="ToolRegistry"/>），并返回该 agent 勾选的工具里
    /// 「此刻仍未在 ToolRegistry 注册」的名字列表（MCP 未连接/连接失败时非空——这些工具重建 agent 也拿不到）。
    /// </summary>
    private async Task<string[]> EnsureMcpReadyAsync(IEnumerable<string> selectedTools, CancellationToken ct)
    {
        if (_toolRegistry is null)
            return Array.Empty<string>();

        var selected = selectedTools.Distinct().ToList();
        string[] Missing() => selected.Where(n => !_toolRegistry.Contains(n)).ToArray();

        if (_mgmt is null)
            return Missing();

        // 先确保已连接（shared-task：若启动时已连过，直接返回）并注册工具。
        try { await _mgmt.EnsureStartedAsync(ct).ConfigureAwait(false); }
        catch { /* 连接失败不阻断——下面用 MissingTools 反映 */ }

        var missingAfterStart = Missing();
        if (missingAfterStart.Length == 0)
            return Array.Empty<string>();

        // 仍缺：可能是启动时连接失败、后来 server 已可连。RefreshAsync 会重读目录 + 重连（置空 _startTask），
        // 把「启动期连失败、现在可连」的 MCP server 恢复出来。重连后还要再 EnsureStartedAsync 把新工具注册进 ToolRegistry
        // （RefreshAsync 本身不注册工具，见 McpManagementService.RefreshAsync）。
        try
        {
            await _mgmt.RefreshAsync(ct).ConfigureAwait(false);
            await _mgmt.EnsureStartedAsync(ct).ConfigureAwait(false);
        }
        catch { /* 重连失败仍不阻断 */ }

        return Missing();
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
        /// <summary>保存后仍未在 ToolRegistry 注册的工具名（通常是 MCP server 未连接/连接失败）。非空 ≠ 保存失败，只是这些工具 agent 暂无法调用。</summary>
        public string[]? MissingTools { get; set; }
    }

    public sealed class ReloadOutput
    {
        public bool Success { get; set; }
        public string? AgentId { get; set; }
        public string? Error { get; set; }
        /// <summary>重建后仍未在 ToolRegistry 注册的工具名（通常是 MCP server 未连接/连接失败）。非空 = 这些工具 agent 暂无法调用。</summary>
        public string[]? MissingTools { get; set; }
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
        /// <summary>已勾选但未注册进 ToolRegistry 的工具名（MCP server 未连接时非空）。</summary>
        public string[]? MissingTools { get; set; }
        public string? Error { get; set; }
    }

    [Description("查询一个自定义 agent 的当前定义（缺省定位「正在调用本工具的当前 agent」）。只读，无副作用。")]
    public async Task<CurrentAgentOutput> GetCurrentAgentAsync(
        [Description("目标 agent 的名字。留空则定位「正在调用本工具的当前 agent」。")] string? agent = null,
        CancellationToken cancellationToken = default)
    {
        var (def, label) = await ResolveTargetAsync(agent, cancellationToken).ConfigureAwait(false);
        if (def is null)
        {
            var hint = string.IsNullOrWhiteSpace(agent)
                ? "不在 agent 运行上下文内；请显式指定 agent 名字。"
                : $"找不到名为「{agent}」的 agent。";
            return new CurrentAgentOutput { Success = false, Error = hint };
        }
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

            // 保存后确保 MCP 就绪（连接 + 工具注册），再重建——否则刚勾选的 MCP 工具因未注册被静默跳过。
            var missing = await EnsureMcpReadyAsync(def.Tools, cancellationToken).ConfigureAwait(false);
            _runner.Invalidate(def.Id); // 若它正在被召唤/运行，重建使其生效

            return new ConfigureOutput { Success = true, Agent = def.Name, MissingTools = missing };
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

        // 重建前确保 MCP 就绪（连接 + 工具注册）——否则勾选了的 MCP 工具因未注册被 ResolveAll 静默跳过。
        var missing = await EnsureMcpReadyAsync(resolved.Tools, cancellationToken).ConfigureAwait(false);
        _runner.Invalidate(resolved.Id);
        return new ReloadOutput { Success = true, AgentId = resolved.Id.ToString(), MissingTools = missing };
    }
}
