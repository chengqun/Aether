using System.Diagnostics;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Aether.AI.Mcp;

/// <summary>
/// 管理所有已配置 MCP server 的连接与工具。宿主启动时调用 <see cref="StartAsync"/> 连接各 server 并预解析工具；
/// <see cref="GetTools"/> 供 ToolRegistry 注册（专家选择器勾选）；<see cref="GetServers"/> 供管理 UI 展示状态+工具。
/// <para>配置经 <see cref="_configProvider"/> 获取——<see cref="RefreshAsync"/> 重读目录，支持增删改后重连。</para>
/// <para>单个 server 连接失败记 Error 并跳过，不阻断其它 server。</para>
/// </summary>
public sealed class McpServerRegistry : IMcpServerRegistry, IAsyncDisposable
{
    private readonly Func<IEnumerable<McpServerConfig>> _configProvider;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly List<Entry> _entries = new();
    private int _started;

    public McpServerRegistry(Func<IEnumerable<McpServerConfig>> configProvider, ILoggerFactory? loggerFactory = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _loggerFactory = loggerFactory;
    }

    public bool IsStarted => Volatile.Read(ref _started) == 1;

    public IReadOnlyList<AIFunction> GetTools() => _entries.SelectMany(e => e.Tools).ToList();

    public IReadOnlyList<McpServerEntry> GetServers()
        => _entries.Select(e => new McpServerEntry
        {
            Config = e.Config,
            IsConnected = e.IsConnected,
            Tools = e.Tools,
            Error = e.Error,
        }).ToList();

    /// <summary>连接所有已配置 server 并预解析工具。已启动则幂等返回。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        await ConnectAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重读目录并重连所有 server（关闭旧 client）。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await DisposeEntriesAsync().ConfigureAwait(false);
        Volatile.Write(ref _started, 0);
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        await ConnectAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        var log = _loggerFactory?.CreateLogger<McpServerRegistry>();
        _entries.Clear();
        foreach (var cfg in _configProvider())
        {
            var entry = new Entry(cfg);
            _entries.Add(entry);
            try
            {
                entry.Client = await ConnectAsync(cfg, cancellationToken).ConfigureAwait(false);
                var tools = await entry.Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (var t in tools)
                    if (t is AIFunction fn) entry.Tools.Add(fn);
                log?.LogInformation("已连接 MCP server {Name}，工具 {Count} 个。", cfg.Name, tools.Count);
            }
            catch (Exception ex)
            {
                // 透传完整异常消息链（含 inner），便于定位「command not found / 编译失败 / 握手超时」等真实原因。
                var sb = new System.Text.StringBuilder();
                for (var e = ex; e is not null; e = e.InnerException)
                {
                    if (sb.Length > 0) sb.Append(" → ");
                    sb.Append(e.Message);
                }
                entry.Error = sb.ToString();
                log?.LogWarning(ex, "连接 MCP server {Name} 失败，跳过。", cfg.Name);
            }
        }
    }

    private static async Task<McpClient> ConnectAsync(McpServerConfig cfg, CancellationToken ct)
    {
        if (cfg.IsStdio)
        {
            // CWD：未配置时用用户主目录，避免从 .app 启动时 CWD=/（只读）导致子进程异常。
            var workingDirectory = !string.IsNullOrEmpty(cfg.WorkingDirectory)
                ? cfg.WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string command;
            List<string> arguments;
            if (Path.IsPathRooted(cfg.Command))
            {
                // 绝对路径（编译好的二进制）直接跑。
                command = cfg.Command!;
                arguments = cfg.Arguments?.ToList() ?? new List<string>();
            }
            else
            {
                // GUI 启动的 app 继承受限 PATH（缺 /usr/local/bin、/opt/homebrew/bin 等），Process.Start("dotnet")
                // 用父进程 PATH 解析命令——子进程 env 的 PATH 改了也来不及。故相对命令（dotnet/npx/python3）
                // 经登录 shell 跑（shell source profile 拿到完整 PATH 再 exec），像 Trae/VS Code 那样。
                var shell = Environment.GetEnvironmentVariable("SHELL");
                if (!string.IsNullOrEmpty(shell))
                {
                    command = shell;
                    var cmd = ShellQuote(cfg.Command!) + " " +
                              string.Join(" ", (cfg.Arguments ?? Array.Empty<string>()).Select(ShellQuote));
                    arguments = new List<string> { "-l", "-c", cmd };
                }
                else
                {
                    // Windows / 无 SHELL：相对命令直接跑（系统 PATH 通常已含 dotnet）。
                    command = cfg.Command!;
                    arguments = cfg.Arguments?.ToList() ?? new List<string>();
                }
            }

            var opts = new StdioClientTransportOptions
            {
                Command = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
            };
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

        throw new InvalidOperationException($"MCP server '{cfg.Name}' 既未配置 Command(stdio) 也未配置 Url(http)。");
    }

    /// <summary>POSIX 单引号转义：把参数包成 '…'，内部 ' 转义为 '\''。</summary>
    private static string ShellQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    public async ValueTask DisposeAsync() => await DisposeEntriesAsync().ConfigureAwait(false);

    private async Task DisposeEntriesAsync()
    {
        foreach (var e in _entries)
        {
            if (e.Client is not null)
            {
                try { await e.Client.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
        _entries.Clear();
    }

    private sealed class Entry
    {
        public McpServerConfig Config { get; }
        public McpClient? Client { get; set; }
        public List<AIFunction> Tools { get; } = new();
        public string? Error { get; set; }
        public bool IsConnected => Client is not null && Error is null;
        public Entry(McpServerConfig config) => Config = config;
    }
}
