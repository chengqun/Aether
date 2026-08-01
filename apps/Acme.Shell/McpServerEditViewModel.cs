using Aether.AI.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// MCP server 编辑 ViewModel：单个 JSON 文本框，粘贴完整 server 配置即可。
/// 编辑现有时回填该 server 的 JSON；保存时解析校验，失败在 <see cref="Error"/> 提示。
/// </summary>
public partial class McpServerEditViewModel : ObservableObject
{
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _json = DefaultTemplate;
    [ObservableProperty] private string? _error;

    public event Action<McpServerConfig>? SaveRequested;
    public event Action? CancelRequested;

    public McpServerEditViewModel(McpServerConfig? existing = null)
    {
        if (existing is not null)
        {
            IsEditing = true;
            Json = existing.ToJson();
        }
    }

    [RelayCommand]
    private void Save()
    {
        var config = McpServerConfig.TryParse(Json);
        if (config is null || string.IsNullOrWhiteSpace(config.Name))
        {
            Error = "JSON 解析失败或缺少 name 字段。示例见模板。";
            return;
        }
        Error = null;
        SaveRequested?.Invoke(config);
    }

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke();

    private const string DefaultTemplate = """
{
  "mcpServers": {
    "my-server": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
      "env": {}
    }
  }
}
""";
}
