using System.Text;
using System.Text.Json;
using Acme.AppAutomation.Domain;
using Aether.AI.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpClientType = ModelContextProtocol.Client.McpClient;

namespace Acme.AppAutomation.McpClientHost;

/// <summary>
/// <see cref="IMcpToolInvoker"/> 的真实实现：自己 new <see cref="McpClient"/> 连桌面控制 MCP server（独立连，
/// 不复用 <c>McpServerRegistry</c>），按工具名直调。配置取自 <c>mcp-servers/desktop.json</c> 的 <c>desktop</c> 条目
/// （<see cref="McpServerConfig"/>，与 <c>McpServerRegistry</c> 同构）。惰性连接：首次调用触发 <see cref="EnsureConnectedAsync"/>。
/// <para>工具名不在此绑定具体 server —— 由 <c>McpDesktopAutomation</c> 决定（click/type/get_ui_tree…），
/// <c>desktop.json</c> 指向工具名一致的 server（Windows 期用 FlaUI-MCP）。本类只负责「连 + 按名调 + 取文本」。</para>
/// <para>MCP alpha 包（<c>ModelContextProtocol</c>）隔离在本项目，AppAutomation 通用层不引；宿主在 Windows 侧注册本类。</para>
/// </summary>
public sealed class McpDesktopToolInvoker : IMcpToolInvoker, IAsyncDisposable
{
    private readonly McpServerConfig _config;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<McpDesktopToolInvoker>? _log;

    private McpClientType? _client;
    private Task? _connectTask;
    private string? _connectError;
    private bool _disposed;
    private readonly object _connectLock = new();

    public McpDesktopToolInvoker(McpServerConfig config, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loggerFactory = loggerFactory;
        _log = loggerFactory?.CreateLogger<McpDesktopToolInvoker>();
    }

    /// <summary>已建连且未释放（不探活；连接失败后 <see cref="CallToolAsync"/> 会重试一次）。</summary>
    public bool IsConnected => _client is not null && !_disposed && _connectError is null;

    public async Task<string> CallToolAsync(string toolName, object? args = null, CancellationToken ct = default)
    {
        var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (client is null)
            return ErrorJson($"桌面控制后端未连接：{_connectError}");

        try
        {
            // MCP SDK 1.2.0 CallToolAsync(name, IReadOnlyDictionary<string,object?>?, IProgress<...>?, CancellationToken)。
            // args 经 JSON 往返成 JsonElement 字典（object? 装箱结构体）最稳，兼容匿名对象/Dictionary/已序列化串。
            var argDict = MapArgs(args);
            var result = await client.CallToolAsync(toolName, argDict, progress: null, cancellationToken: ct).ConfigureAwait(false);

            if (result.IsError is true)
                return ErrorJson(ExtractText(result));

            return ExtractText(result);
        }
        catch (Exception ex)
        {
            _connectError = ex.Message;  // 标记掉线，下次调用重试连接
            _log?.LogWarning(ex, "调 MCP 工具 {Tool} 失败。", toolName);
            return ErrorJson($"工具 {toolName} 调用异常：{ex.Message}");
        }
    }

    // ─────────────────────────── 连接（shared-task，同 McpServerRegistry 模式）───────────────────────────

    /// <summary>确保已连接；返回 client，连接失败返回 null 并记 <see cref="_connectError"/>。</summary>
    public Task<McpClientType?> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_client is not null && _connectError is null) return Task.FromResult(_client);
        lock (_connectLock)
        {
            if (_client is not null && _connectError is null) return Task.FromResult<McpClientType?>(_client);
            _connectTask ??= ConnectAsync(ct);
            return WaitConnectAsync(_connectTask);
        }
    }

    private async Task<McpClientType?> WaitConnectAsync(Task connectTask)
    {
        try { await connectTask.ConfigureAwait(false); }
        catch (Exception ex) { _connectError = ex.Message; }
        return _connectError is null ? _client : null;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            _client = await CreateClientAsync(_config, ct).ConfigureAwait(false);
            _connectError = null;
            _log?.LogInformation("桌面控制 MCP server 已连接（{Transport}）。", _config.TransportDisplay);
        }
        catch (Exception ex)
        {
            _client = null;
            // 透传完整异常链（command not found / 握手超时 等真实原因）。
            var sb = new StringBuilder();
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(e.Message);
            }
            _connectError = sb.ToString();
            _log?.LogWarning(ex, "连接桌面控制 MCP server 失败。");
        }
    }

    private static async Task<McpClientType> CreateClientAsync(McpServerConfig cfg, CancellationToken ct)
    {
        if (cfg.IsStdio)
        {
            // CWD：未配置时用用户主目录，避免从受限 PATH 启动时子进程异常。
            var workingDirectory = !string.IsNullOrEmpty(cfg.WorkingDirectory)
                ? cfg.WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string command;
            List<string> arguments;
            if (Path.IsPathRooted(cfg.Command))
            {
                command = cfg.Command!;
                arguments = cfg.Arguments?.ToList() ?? new List<string>();
            }
            else
            {
                // GUI 启动的 app 继承受限 PATH。POSIX 上相对命令经登录 shell 跑（source profile 拿完整 PATH）；
                // Windows 无 SHELL env，相对命令（dotnet）直接跑（系统 PATH 通常已含 dotnet）。
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
                foreach (var kv in cfg.Env) opts.EnvironmentVariables[kv.Key] = kv.Value;

            return await McpClientType.CreateAsync(new StdioClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        if (cfg.IsSse)
        {
            var opts = new HttpClientTransportOptions { Endpoint = new Uri(cfg.Url!) };
            if (cfg.Headers is not null)
                foreach (var kv in cfg.Headers) opts.AdditionalHeaders.Add(kv.Key, kv.Value);
            return await McpClientType.CreateAsync(new HttpClientTransport(opts), cancellationToken: ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"MCP server '{cfg.Name}' 既未配置 Command(stdio) 也未配置 Url(http)。");
    }

    /// <summary>POSIX 单引号转义。</summary>
    private static string ShellQuote(string s)
        => string.IsNullOrEmpty(s) ? "''" : "'" + s.Replace("'", "'\\''") + "'";

    // ─────────────────────────── 参数 / 结果 映射 ───────────────────────────

    /// <summary>
    /// 把 args（匿名对象 / Dictionary / 已序列化 JSON 串 / null）转成 MCP <c>CallToolAsync</c> 接受的
    /// <see cref="Dictionary{TKey, TValue}"/>（string→object?，值为 <see cref="JsonElement"/>）。经 JSON 往返统一形态。
    /// </summary>
    private static Dictionary<string, object?> MapArgs(object? args)
    {
        if (args is null) return new Dictionary<string, object?>();

        JsonElement element;
        if (args is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object?>();
            using var doc = JsonDocument.Parse(s);
            element = doc.RootElement.Clone();
        }
        else
        {
            element = JsonSerializer.SerializeToElement(args);
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
                dict[prop.Name] = prop.Value.Clone();   // JsonElement 装箱为 object?
        }
        return dict;
    }

    /// <summary>从 <see cref="CallToolResult"/> 抽取所有文本内容块拼成字符串（MCP 工具结果的标准载体）。</summary>
    private static string ExtractText(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var content in result.Content)
        {
            // TextContentBlock 是 MCP 文本结果；其它块（image/resource）跳过——固定过程只消费文本。
            if (content is TextContentBlock text && !string.IsNullOrEmpty(text.Text))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    private static string ErrorJson(string msg)
        => "{\"error\":" + JsonSerializer.Serialize(msg) + "}";

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); }
            catch { /* 释放期忽略 */ }
        }
    }
}
