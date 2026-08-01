namespace Aether.AI.Mcp;

/// <summary>
/// 一个 MCP server 的连接配置。支持 stdio（本地子进程）或 SSE（远程 HTTP）两种 transport。
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>server 名称（仅标识用）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>stdio transport：可执行文件命令（如 "npx"、"python3"）。设置时走 stdio。</summary>
    public string? Command { get; init; }

    /// <summary>stdio 命令参数。</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>stdio 工作目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>stdio 子进程环境变量（null = 继承当前进程）。</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>SSE transport：远程 endpoint URL。设置时走 SSE。</summary>
    public string? Url { get; init; }

    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);
    public bool IsSse => !string.IsNullOrWhiteSpace(Url);
}
