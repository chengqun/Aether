using System.ComponentModel;
using Aether.AI.Abstractions;

namespace Acme.Mobile.Shared.Hosting.Tools;

/// <summary>
/// 手机端 MCP server 配置工具（<c>mcp_list_servers</c>/<c>mcp_add_server</c>/<c>mcp_remove_server</c>）：
/// **只做文件 CRUD on labs/mcp-servers/*.json（权威存储），不实连**——iOS 沙盒禁止 spawn 子进程，
/// 手机端无 <c>IMcpManagementService</c>（impl 在 Aether.AI.Mcp，拉 alpha ModelContextProtocol）。
/// 写盘即「创建」，labs 是权威存储（云同步后桌面端可见并连接）——与 <c>McpCreateViewModel</c> 同构。
/// add/remove 是能力注入/卸载的写入口 → Dangerous；list 只读 Safe。
/// </summary>
public sealed class MobileMcpConfigTool
{
    private readonly Func<string?> _labsRoot;

    public MobileMcpConfigTool(Func<string?> labsRoot)
    {
        _labsRoot = labsRoot;
    }

    private string? McpDir()
    {
        var labs = _labsRoot();
        if (string.IsNullOrWhiteSpace(labs)) return null;
        return Path.Combine(labs, "mcp-servers");
    }

    public sealed class ServerItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Transport { get; set; }
        public string? Command { get; set; }
        public string? Url { get; set; }
    }

    public sealed class ListOutput
    {
        public int Count { get; set; }
        public List<ServerItem> Servers { get; set; } = new();
        public string? Error { get; set; }
    }

    public sealed class MutateOutput
    {
        public bool Success { get; set; }
        public string? Name { get; set; }
        public string? Error { get; set; }
    }

    [Description("列出当前所有 MCP server 配置文件（labs/mcp-servers/*.json）：名称、传输方式（stdio/http）。只读。" +
                 "注意：手机端只管理配置（权威存储），连接在桌面端生效。添加/删除前先调此确认现有配置，避免重名覆盖。")]
    public Task<ListOutput> ListServersAsync(CancellationToken cancellationToken = default)
    {
        var dir = McpDir();
        if (dir is null)
            return Task.FromResult(new ListOutput { Error = "未配置 labs 目录。" });
        if (!Directory.Exists(dir))
            return Task.FromResult(new ListOutput());

        var servers = new List<ServerItem>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            McpServerConfig? cfg = null;
            try
            {
                var json = File.ReadAllText(f);
                cfg = McpServerConfig.TryParse(json);
            }
            catch { /* 跳过读/解析失败的配置 */ }

            var name = cfg?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(f);
            servers.Add(new ServerItem
            {
                Name = name,
                Command = cfg?.Command,
                Url = cfg?.Url,
                Transport = cfg?.TransportDisplay,
            });
        }
        return Task.FromResult(new ListOutput { Count = servers.Count, Servers = servers });
    }

    [Description("新增或更新一个 MCP server 配置（写到 labs/mcp-servers/<name>.json，与 Claude Code .mcp.json 格式兼容）。" +
                 "stdio 用 command+args（本地命令），http 用 url（远程）。写盘即创建（权威存储，云同步后桌面端可见并连接）。需要用户审批确认。")]
    public async Task<MutateOutput> AddServerAsync(
        [Description("server 名称（配置文件名，建议英文短横线）")] string name,
        [Description("stdio 命令（可选；stdio 与 url 二选一）")] string? command = null,
        [Description("stdio 参数（可选，JSON 字符串数组，如 [\"--stdio\"]）")] string? argsJson = null,
        [Description("http url（可选；与 command 二选一，如 https://example.com/mcp）")] string? url = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new MutateOutput { Error = "name 不能为空。" };
        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
            return new MutateOutput { Error = "command 与 url 至少填一个（stdio 或 http）。" };

        var dir = McpDir();
        if (dir is null)
            return new MutateOutput { Error = "未配置 labs 目录。" };

        try
        {
            var cfg = new McpServerConfig { Name = name.Trim() };
            if (!string.IsNullOrWhiteSpace(command))
            {
                cfg.Command = command;
                cfg.Arguments = ParseArgs(argsJson);
            }
            else
            {
                cfg.Url = url;
            }

            Directory.CreateDirectory(dir);
            var full = Path.Combine(dir, Sanitize(name) + ".json");
            await File.WriteAllTextAsync(full, cfg.ToJson(), cancellationToken).ConfigureAwait(false);
            return new MutateOutput { Success = true, Name = name.Trim() };
        }
        catch (Exception ex)
        {
            return new MutateOutput { Error = ex.Message };
        }
    }

    [Description("删除一个 MCP server 配置（按名删 labs/mcp-servers/<name>.json）。需要用户审批确认。")]
    public Task<MutateOutput> RemoveServerAsync(
        [Description("要删除的 server 名称。")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new MutateOutput { Error = "name 不能为空。" });
        var dir = McpDir();
        if (dir is null)
            return Task.FromResult(new MutateOutput { Error = "未配置 labs 目录。" });

        var full = Path.Combine(dir, Sanitize(name) + ".json");
        if (File.Exists(full)) File.Delete(full);
        return Task.FromResult(new MutateOutput { Success = true, Name = name.Trim() });
    }

    private static List<string> ParseArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                return doc.RootElement.EnumerateArray()
                    .Select(x => x.ValueKind == System.Text.Json.JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                    .ToList();
        }
        catch { /* 容错：非法 argsJson → 空参数 */ }
        return new();
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var trimmed = new string(chars).Trim();
        return string.IsNullOrEmpty(trimmed) ? "server" : trimmed;
    }
}
