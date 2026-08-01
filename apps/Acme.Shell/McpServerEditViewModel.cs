using Aether.AI.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// MCP server 编辑表单 ViewModel（新增/编辑复用）。字段：名称、传输方式(stdio/http)、
/// command、args(多行→list)、url、env(KEY=VALUE/行)、headers(KEY: VALUE/行)。
/// 保存时构造 <see cref="McpServerConfig"/> 并经 <see cref="SaveRequested"/> 回传（由 View 提交并关窗）。
/// </summary>
public partial class McpServerEditViewModel : ObservableObject
{
    /// <summary>是否编辑现有（true）还是新建（false）。</summary>
    [ObservableProperty] private bool _isEditing;

    [ObservableProperty] private string _name = string.Empty;
    /// <summary>true=stdio(command+args)，false=http(url)。</summary>
    [ObservableProperty] private bool _isStdio = true;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _arguments = string.Empty; // 多行，每行一个参数
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _env = string.Empty;       // KEY=VALUE 每行
    [ObservableProperty] private string _headers = string.Empty;   // KEY: VALUE 每行

    /// <summary>保存事件：回传构造好的配置，由 View 提交后关窗。</summary>
    public event Action<McpServerConfig>? SaveRequested;
    /// <summary>取消事件：关窗。</summary>
    public event Action? CancelRequested;

    public McpServerEditViewModel(McpServerConfig? existing = null)
    {
        if (existing is not null)
        {
            IsEditing = true;
            Name = existing.Name;
            IsStdio = existing.IsStdio;
            Command = existing.Command ?? "";
            Arguments = existing.Arguments is null ? "" : string.Join("\n", existing.Arguments);
            Url = existing.Url ?? "";
            Env = MapToString(existing.Env, "=");
            Headers = MapToString(existing.Headers, ": ");
            _editingName = existing.Name;
        }
    }

    /// <summary>编辑时禁止改名（按 name 定位配置文件）。</summary>
    private readonly string? _editingName;
    public bool CanEditName => !IsEditing;

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        var config = ToConfig();
        SaveRequested?.Invoke(config);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    public McpServerConfig ToConfig()
    {
        var name = (IsEditing ? _editingName : Name) ?? Name ?? "";
        return new McpServerConfig
        {
            Name = name.Trim(),
            Command = IsStdio ? (string.IsNullOrWhiteSpace(Command) ? null : Command.Trim()) : null,
            Arguments = IsStdio ? ParseLines(Arguments) : null,
            Url = !IsStdio ? (string.IsNullOrWhiteSpace(Url) ? null : Url.Trim()) : null,
            Env = IsStdio ? ParseMap(Env, "=") : null,
            Headers = !IsStdio ? ParseMap(Headers, ":") : null,
        };
    }

    private static List<string> ParseLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    private static IReadOnlyDictionary<string, string>? ParseMap(string text, string separator)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(separator, StringComparison.Ordinal);
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + separator.Length)..].Trim();
            if (!string.IsNullOrEmpty(key)) dict[key] = val;
        }
        return dict.Count == 0 ? null : dict;
    }

    private static string MapToString(IReadOnlyDictionary<string, string>? map, string separator)
    {
        if (map is null || map.Count == 0) return "";
        return string.Join("\n", map.Select(kv => $"{kv.Key}{separator}{kv.Value}"));
    }
}
