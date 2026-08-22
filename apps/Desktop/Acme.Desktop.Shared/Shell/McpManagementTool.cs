using System.ComponentModel;
using System.Text.Json;
using Aether.AI.Abstractions;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// <c>mcp_list_servers</c> / <c>mcp_add_server</c> / <c>mcp_remove_server</c> / <c>mcp_refresh</c> 工具：
/// 让 agent 复用专家页「MCP工具」tab 的同一套 <see cref="IMcpManagementService"/>，动态增删 MCP server。
/// 与 <see cref="GitTool"/> 同属宿主层注册——<see cref="IMcpManagementService"/> 的实现
/// (<c>Aether.AI.Mcp.McpManagementService</c>) 在平台层，不在任何 ai/ 业务上下文，故不走 ai/ 适配器。
/// </summary>
/// <remarks>
/// <see cref="SaveServerAsync"/> 内部已「写 json → RefreshAsync 重连 → RegisterMcpTool 注册工具」全程闭环，
/// 并触发 <c>Changed</c> → <see cref="McpAutoAssignToAllAgents"/> 订阅后「自动勾选进所有 agent」（手动/AI 两路径共用）。
/// 故本工具只负责装/删/查 server，不单独做勾选。装/删标 <c>Dangerous</c>（能力注入/卸载，须审批）。
/// </remarks>
public sealed class McpManagementTool
{
    private readonly IMcpManagementService _mgmt;

    public McpManagementTool(IMcpManagementService mgmt,
        // 注入仅为了强制 McpAutoAssignToAllAgents 被 DI 解析构造（单例懒加载——不解析则其 Changed 订阅永不挂上）。
        McpAutoAssignToAllAgents _)
    {
        _mgmt = mgmt;
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

    [Description("新增或更新一个 MCP server 配置，写盘后立即重连并把其工具注册进可用工具池（自动勾选进所有 agent，下轮可用）。" +
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
            return new MutateOutput { Success = true, Name = parsed.Name };
        }
        catch (Exception ex)
        {
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
}
