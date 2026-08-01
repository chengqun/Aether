using Microsoft.Extensions.AI;

namespace Aether.AI.Hosting;

/// <summary>单个 MCP server 的连接结果（低层，registry 返回；<see cref="Tools"/> 为 MEAI AIFunction）。</summary>
public sealed class McpServerEntry
{
    public required McpServerConfig Config { get; init; }
    public bool IsConnected { get; init; }
    public IReadOnlyList<AIFunction> Tools { get; init; } = Array.Empty<AIFunction>();
    public string? Error { get; init; }
}

/// <summary>MCP 工具的 UI DTO（name + 描述）。</summary>
public sealed record McpToolInfo(string Name, string Description);

/// <summary>MCP server 的 UI DTO（配置摘要 + 连接状态 + 工具列表 + 原始配置供编辑回填）。</summary>
public sealed record McpServerStatus(
    string Name,
    string Transport,
    bool IsConnected,
    string? Error,
    IReadOnlyList<McpToolInfo> Tools,
    McpServerConfig Config);

/// <summary>
/// MCP 管理服务（VM 面向）：读写 <c>mcp-servers/</c> 配置 + 查询连接状态/工具。
/// impl 在 <c>Aether.AI.Mcp</c> 项目；接口在 Hosting 供 VM（不引 alpha）使用。
/// </summary>
public interface IMcpManagementService
{
    /// <summary>确保 MCP server 已连接 + 工具已注册进 ToolRegistry（幂等，shared-task 等到连接完成）。</summary>
    Task EnsureStartedAsync(CancellationToken cancellationToken = default);

    /// <summary>当前所有 server 的状态 + 工具（含未连接的，带 Error）。</summary>
    Task<IReadOnlyList<McpServerStatus>> GetServersAsync(CancellationToken cancellationToken = default);

    /// <summary>新增/更新一个 server 配置（按 name 写 <c>mcp-servers/&lt;name&gt;.json</c>）并重连。</summary>
    Task SaveServerAsync(McpServerConfig config, CancellationToken cancellationToken = default);

    /// <summary>删除一个 server 配置（删文件）并重连。</summary>
    Task DeleteServerAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>重连所有 server（重读目录）。</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
