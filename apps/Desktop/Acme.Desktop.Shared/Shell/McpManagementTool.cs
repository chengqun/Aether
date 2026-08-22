using System.ComponentModel;
using System.Text.Json;
using Acme.Notes.Domain.Agents;
using Aether.AI.Abstractions;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>mcp_list_servers</c> / <c>mcp_add_server</c> / <c>mcp_remove_server</c> / <c>mcp_refresh</c> 工具：
/// 让 agent 复用专家页「MCP工具」tab 的同一套 <see cref="IMcpManagementService"/>，动态增删 MCP server。
/// 与 <see cref="GitTool"/> 同属宿主层注册——<see cref="IMcpManagementService"/> 的实现
/// (<c>Aether.AI.Mcp.McpManagementService</c>) 在平台层，不在任何 ai/ 业务上下文，故不走 ai/ 适配器。
/// </summary>
/// <remarks>
/// <see cref="SaveServerAsync"/> 内部已「写 json → RefreshAsync 重连 → RegisterMcpTool 注册工具」全程闭环。
/// <c>mcp_add_server</c> 额外做「默认勾选」：装完把新 server 的工具自动加进当前 agent 的 Tools + RefreshAgent 重建，
/// 使 agent 无需再手动勾选即可用上（装即默认可选，不想用再移除/取消）。全程 try/catch 兜底——任何一步失败只返回
/// Error，绝不把异常穿透到宿主导致 app 崩溃。
/// </remarks>
public sealed class McpManagementTool
{
    private readonly IMcpManagementService _mgmt;
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;

    public McpManagementTool(IMcpManagementService mgmt, ICustomAgentCatalog catalog, ICustomAgentRunner runner)
    {
        _mgmt = mgmt;
        _catalog = catalog;
        _runner = runner;
    }

    public sealed class ServerItem
    {
        public string Name { get; set; } = string.Empty;
        public string Transport { get; set; } = string.Empty;
        public bool IsConnected { get; set; }
        public string? Error { get; set; }
        public List<string> Tools { get; set; } = new();
    }

    public sealed class ListOutput
    {
        public int Count { get; set; }
        public List<ServerItem> Servers { get; set; } = new();
    }

    public sealed class MutateOutput
    {
        public bool Success { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出当前所有已配置的 MCP server：名称、传输方式（stdio/http）、连接状态、工具清单。只读，无副作用。" +
                 "添加/删除 MCP server 前先调此工具确认现有配置，避免重名覆盖。")]
    public async Task<ListOutput> ListServersAsync(CancellationToken cancellationToken = default)
    {
        var list = await _mgmt.GetServersAsync(cancellationToken).ConfigureAwait(false);
        return new ListOutput
        {
            Count = list.Count,
            Servers = list.Select(s => new ServerItem
            {
                Name = s.Name,
                Transport = s.Transport,
                IsConnected = s.IsConnected,
                Error = s.Error,
                Tools = s.Tools.Select(t => t.Name).ToList(),
            }).ToList(),
        };
    }

    [Description("新增或更新一个 MCP server 配置，写盘后立即重连、把其工具注册进可用工具池并默认勾选给当前 agent（下轮起可用）。" +
                 "config 为一段 JSON（与 Claude Code 的 .mcp.json 格式兼容）：stdio 用 command+args（+可选 cwd/env），" +
                 "HTTP 用 url（+可选 headers），顶层需含 name 或套在 \"mcpServers\": { \"name\": {...} } 映射里。" +
                 "此操作会给 agent 安装新工具/新能力，属不可轻率放行的写入口——需要用户审批确认。" +
                 "示例：{\"mcpServers\":{\"my-server\":{\"command\":\"npx\",\"args\":[\"-y\",\"some-mcp-server\"]}}}")]
    public async Task<MutateOutput> AddServerAsync(
        [Description("MCP server 配置 JSON。兼容 .mcp.json 格式：command+args（stdio）或 url（http）；name 必填。")] string config,
        CancellationToken cancellationToken = default)
    {
        var parsed = McpServerConfig.TryParse(config);
        if (parsed is null)
            return new MutateOutput { Success = false, Error = "配置 JSON 解析失败——必须是单个 server 对象、mcpServers 映射或数组之一。" };
        if (string.IsNullOrWhiteSpace(parsed.Name))
            return new MutateOutput { Success = false, Error = "配置缺少 name 字段。" };

        try
        {
            await _mgmt.SaveServerAsync(parsed, cancellationToken).ConfigureAwait(false);

            // 装完默认勾选到当前 agent：拿新 server 的工具名，加进当前 agent 的 Tools 并重建。
            // 这一步是「好坏消息」：默认可用；若定位不到当前 agent（非 agent 上下文）则跳过勾选，仅装 server 本身。
            var autoCheck = await TryAutoCheckIntoSelfAsync(parsed.Name, cancellationToken).ConfigureAwait(false);

            return new MutateOutput
            {
                Success = true,
                Name = parsed.Name,
                Error = autoCheck, // null=已自动勾选；非 null=装了但未自动勾选的原因（不阻断）
            };
        }
        catch (Exception ex)
        {
            // 兜底：任何异常（含 SaveServerAsync 抛的）都不穿透导致 app 崩溃。
            return new MutateOutput { Success = false, Name = parsed.Name, Error = $"添加失败：{ex.Message}" };
        }
    }

    [Description("删除一个已配置的 MCP server（按 name 删配置文件）并重连。name 传 server 名（非文件名）。" +
                 "此操作会卸载该 server 提供的工具，属写入口——需要用户审批确认。")]
    public async Task<MutateOutput> RemoveServerAsync(
        [Description("要删除的 MCP server 名。")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new MutateOutput { Success = false, Error = "name 不能为空。" };

        try
        {
            await _mgmt.DeleteServerAsync(name, cancellationToken).ConfigureAwait(false);
            return new MutateOutput { Success = true, Name = name };
        }
        catch (Exception ex)
        {
            return new MutateOutput { Success = false, Name = name, Error = $"删除失败：{ex.Message}" };
        }
    }

    [Description("重连所有 MCP server（重读 mcp-servers 目录）。" +
                 "当手动改过配置目录里的 json、或某 server 掉线需重连时调用。只读目录并重连，无配置写入。")]
    public async Task<MutateOutput> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _mgmt.RefreshAsync(cancellationToken).ConfigureAwait(false);
            return new MutateOutput { Success = true };
        }
        catch (Exception ex)
        {
            return new MutateOutput { Success = false, Error = $"重连失败：{ex.Message}" };
        }
    }

    /// <summary>
    /// 装完 server 后尝试把它提供的工具默认勾选进「当前正在运行的 agent」并重建。
    /// 成功返回 null；勾选被跳过（非 agent 上下文 / 找不到定义 / 无工具）返回说明文字，不抛异常。
    /// </summary>
    private async Task<string?> TryAutoCheckIntoSelfAsync(string serverName, CancellationToken cancellationToken)
    {
        var agentIdText = AgentExecutionContext.CurrentAgentId.Value;
        if (string.IsNullOrEmpty(agentIdText) || !Guid.TryParse(agentIdText, out var agentId))
            return "已连接，但运行上下文无当前 agent，未自动勾选（可在专家页手动勾选）。";

        try
        {
            // 该 server 的工具名（按 name 过滤）。
            var servers = await _mgmt.GetServersAsync(cancellationToken).ConfigureAwait(false);
            var tools = servers.Where(s => s.Name == serverName)
                               .SelectMany(s => s.Tools)
                               .Select(t => t.Name)
                               .Where(n => !string.IsNullOrWhiteSpace(n))
                               .Distinct()
                               .ToList();
            if (tools.Count == 0)
                return "已连接，但该 server 未解析出任何工具，无需勾选。";

            // 照页面 ExpertsViewModel.SaveAsync 的健壮模式：读现定义 → 只改 Tools → 回写。
            var def = await _catalog.GetAsync(agentId, cancellationToken).ConfigureAwait(false);
            if (def is null)
                return "已连接，但找不到当前 agent 定义，未自动勾选。";

            var merged = new HashSet<string>(def.Tools);
            foreach (var t in tools) merged.Add(t);
            def.Tools = merged.ToList();

            await _catalog.SaveAsync(def, cancellationToken).ConfigureAwait(false);
            _runner.RefreshAgent(agentId); // 温和重建（保留会话，仅换 agent 实例带新 Tools）
            return null; // 成功自动勾选 + 重建
        }
        catch (Exception ex)
        {
            return $"server 已连接，但自动勾选失败：{ex.Message}（可手动勾选）。";
        }
    }
}
