using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Aether.AI.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// MCP server 新建/编辑 VM（AI 设置 → MCP）。字段 = 名称 + transport（stdio: command/args，或 http: url），
/// 构造 <see cref="McpServerConfig"/> 后 <see cref="McpServerConfig.ToJson"/> 写 <c>labs/mcp-servers/&lt;name&gt;.json</c>。
/// **只写配置、不重连**——手机端（iOS 沙盒禁止 spawn 子进程）不跑 MCP 连接；stdio server 的实连在桌面端做，
/// HTTP/SSE 类 server 配置同步到桌面后可连。写盘即「创建」，labs 文件即权威存储（云同步后桌面端可见）。
/// 编辑态经导航参数 <c>name</c> 加载已有 .json 回填，保存覆盖，可删除。
/// </summary>
public partial class McpCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;

    /// <summary>编辑态：原始 server 名。null=新建。</summary>
    private string? _editingName;

    [ObservableProperty] private string _name = string.Empty;
    /// <summary>transport 选择：0=stdio（本地命令）1=http（远程 URL）。</summary>
    [ObservableProperty] private int _transportIndex;
    [ObservableProperty] private string _command = string.Empty;
    /// <summary>args 简单文本：每行一个参数。</summary>
    [ObservableProperty] private string _argsText = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public bool IsStdio => TransportIndex == 0;
    public bool IsHttp => TransportIndex == 1;
    public IReadOnlyList<string> TransportOptions { get; } = new[] { "stdio（本地命令）", "http（远程 URL）" };

    /// <summary>是否编辑态（true=编辑已有 server，显示删除按钮；false=新建）。</summary>
    public bool IsEditing => _editingName != null;

    /// <summary>页标题（编辑/新建）。</summary>
    public string ScreenTitle => IsEditing ? "编辑 MCP 服务器" : "新建 MCP 服务器";

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public McpCreateViewModel(Func<string?> labsRoot)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
    }

    /// <summary>接收导航参数：<c>name</c> 非空 = 编辑态（加载已有 .json 回填）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("name", out var raw) && raw is string name && !string.IsNullOrWhiteSpace(name))
        {
            _editingName = name;
            LoadExisting();
        }
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ScreenTitle));
    }

    private void LoadExisting()
    {
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root) || _editingName == null) return;
        var file = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.McpServer).DirectoryName,
            LabsFileWriter.Sanitize(_editingName) + ".json");
        if (!File.Exists(file)) return;
        try
        {
            var cfg = McpServerConfig.TryParse(File.ReadAllText(file));
            if (cfg == null) return;
            Name = cfg.Name;
            if (cfg.IsStdio)
            {
                TransportIndex = 0;
                Command = cfg.Command ?? string.Empty;
                ArgsText = cfg.Arguments is null ? string.Empty : string.Join("\n", cfg.Arguments);
            }
            else
            {
                TransportIndex = 1;
                Url = cfg.Url ?? string.Empty;
            }
        }
        catch { /* 读/解析失败 → 空白表单 */ }
    }

    partial void OnTransportIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStdio));
        OnPropertyChanged(nameof(IsHttp));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "请填写 server 名称";
            return;
        }
        if (IsStdio && string.IsNullOrWhiteSpace(Command))
        {
            StatusMessage = "stdio 需填 command";
            return;
        }
        if (IsHttp && string.IsNullOrWhiteSpace(Url))
        {
            StatusMessage = "http 需填 url";
            return;
        }

        IsLoading = true;
        try
        {
            var config = new McpServerConfig { Name = Name.Trim() };
            if (IsStdio)
            {
                config.Command = Command.Trim();
                config.Arguments = ParseArgs(ArgsText);
            }
            else
            {
                config.Url = Url.Trim();
            }

            var json = config.ToJson();
            var checkError = await Task.Run<string?>(() =>
            {
                var root = _labsRoot();
                if (!string.IsNullOrWhiteSpace(root) && _editingName != null
                    && !string.Equals(LabsFileWriter.Sanitize(_editingName), LabsFileWriter.Sanitize(config.Name), StringComparison.Ordinal))
                {
                    var oldFile = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.McpServer).DirectoryName,
                        LabsFileWriter.Sanitize(_editingName) + ".json");
                    try { if (File.Exists(oldFile)) File.Delete(oldFile); }
                    catch (Exception ex) { return $"删除旧配置失败: {ex.Message}"; }
                }
                LabsFileWriter.WriteAsync(root ?? string.Empty, NoteNodeType.McpServer, config.Name, json).GetAwaiter().GetResult();
                return null;
            });
            if (checkError != null) { StatusMessage = checkError; return; }
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>删除（仅编辑态；删 mcp-servers/<name>.json）。</summary>
    [RelayCommand]
    private void Delete()
    {
        if (_editingName == null) return;
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var file = Path.Combine(root, CatalogNoteDirectory.For(NoteNodeType.McpServer).DirectoryName,
                LabsFileWriter.Sanitize(_editingName) + ".json");
            if (File.Exists(file)) File.Delete(file);
            StatusMessage = "已删除";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
            return;
        }
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
    }

    private static List<string> ParseArgs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
