namespace Aether.AI.Hosting;

/// <summary>
/// 一个 MCP server 的连接配置（纯 POCO，放 Hosting 便于 VM/接口共用，不引 alpha）。
/// 支持 stdio（本地子进程）或 HTTP/SSE（远程）两种 transport。格式与主流 <c>.mcp.json</c> 对齐：
/// <c>command</c>+<c>args</c>(<c>arguments</c>)+<c>env</c>（stdio）或 <c>url</c>+<c>headers</c>（HTTP）。
/// </summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string? Command { get; set; }
    public IReadOnlyList<string>? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public IReadOnlyDictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);
    public bool IsSse => !string.IsNullOrWhiteSpace(Url);

    /// <summary>展示用的 transport 摘要：stdio: &lt;command&gt; / http: &lt;url&gt; / 未配置。</summary>
    public string TransportDisplay =>
        IsStdio ? $"stdio: {Command}" :
        IsSse ? $"http: {Url}" :
        "未配置";
}
