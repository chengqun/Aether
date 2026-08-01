using System.Collections.Concurrent;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Aether.AI.Mcp;

/// <summary>
/// 管理所有已配置 MCP server 的连接与工具。宿主在启动时（桌面 InitializeAsync）调用
/// <see cref="StartAsync"/> 连接各 server 并预解析其工具；<see cref="GetTools"/> 供
/// <c>AgentFactory</c>（经 <c>Func&lt;IReadOnlyList&lt;AIFunction&gt;&gt;</c> 注入）按需取用。
/// <para>单个 server 连接失败记录并跳过，不阻断其它 server 或 agent 构建。</para>
/// <para>工具预解析后缓存（MCP 工具在 server 重启前不变）；<see cref="RefreshAsync"/> 可重连刷新。</para>
/// </summary>
public sealed class McpServerRegistry : IMcpServerRegistry, IAsyncDisposable
{
    private readonly List<McpServerConfig> _configs;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly List<McpClient> _clients = new();
    private IReadOnlyList<AIFunction> _tools = Array.Empty<AIFunction>();
    private int _started;

    public McpServerRegistry(IEnumerable<McpServerConfig> configs, ILoggerFactory? loggerFactory = null)
    {
        _configs = configs?.ToList() ?? new List<McpServerConfig>();
        _loggerFactory = loggerFactory;
    }

    public bool IsStarted => Volatile.Read(ref _started) == 1;

    /// <summary>已解析的 MCP 工具（StartAsync 前为空）。供 AgentFactory 同步取用。</summary>
    public IReadOnlyList<AIFunction> GetTools() => _tools;

    /// <summary>连接所有已配置 server 并预解析工具。已启动则幂等返回。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        var log = _loggerFactory?.CreateLogger<McpServerRegistry>();
        var all = new List<AIFunction>();

        foreach (var cfg in _configs)
        {
            try
            {
                var client = await ConnectAsync(cfg, cancellationToken).ConfigureAwait(false);
                _clients.Add(client);
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var t in tools)
                {
                    if (t is AIFunction fn) all.Add(fn);
                }
                log?.LogInformation("已连接 MCP server {Name}，工具 {Count} 个。", cfg.Name, tools.Count);
            }
            catch (Exception ex)
            {
                // 单个 server 失败不阻断其它。
                log?.LogWarning(ex, "连接 MCP server {Name} 失败，跳过。", cfg.Name);
            }
        }

        _tools = all;
    }

    /// <summary>重新连接并刷新工具（关闭旧 client 再重连）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await DisposeClientsAsync().ConfigureAwait(false);
        Volatile.Write(ref _started, 0);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpClient> ConnectAsync(McpServerConfig cfg, CancellationToken ct)
    {
        if (cfg.IsStdio)
        {
            var opts = new StdioClientTransportOptions
            {
                Command = cfg.Command!,
                Arguments = cfg.Arguments?.ToList() ?? new List<string>(),
            };
            if (!string.IsNullOrEmpty(cfg.WorkingDirectory)) opts.WorkingDirectory = cfg.WorkingDirectory;
            if (cfg.Env is not null)
            {
                foreach (var kv in cfg.Env) opts.EnvironmentVariables[kv.Key] = kv.Value;
            }
            return await McpClient.CreateAsync(new StdioClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        if (cfg.IsSse)
        {
            // 1.2.0：SSE/流式 HTTP 统一走 HttpClientTransport，按 endpoint 自动协商传输模式。
            var opts = new HttpClientTransportOptions { Endpoint = new Uri(cfg.Url!) };
            if (cfg.Headers is not null)
            {
                foreach (var kv in cfg.Headers) opts.AdditionalHeaders.Add(kv.Key, kv.Value);
            }
            return await McpClient.CreateAsync(new HttpClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"MCP server '{cfg.Name}' 既未配置 Command(stdio) 也未配置 Url(sse)。");
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientsAsync().ConfigureAwait(false);
    }

    private async Task DisposeClientsAsync()
    {
        foreach (var c in _clients)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
        _clients.Clear();
        _tools = Array.Empty<AIFunction>();
    }
}
