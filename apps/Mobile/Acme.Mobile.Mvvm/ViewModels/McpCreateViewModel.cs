using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Aether.AI.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// MCP server 新建 VM（AI 设置 → 新建 MCP）。字段 = 名称 + transport（stdio: command/args，或 http: url），
/// 构造 <see cref="McpServerConfig"/> 后 <see cref="McpServerConfig.ToJson"/> 写 <c>labs/mcp-servers/&lt;name&gt;.json</c>。
/// **只写配置、不重连**——手机端（iOS 沙盒禁止 spawn 子进程）不跑 MCP 连接；stdio server 的实连在桌面端做，
/// HTTP/SSE 类 server 配置同步到桌面后可连。写盘即「创建」，labs 文件即权威存储（云同步后桌面端可见）。
/// </summary>
public partial class McpCreateViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;

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

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public McpCreateViewModel(Func<string?> labsRoot)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
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
            await Task.Run(() => LabsFileWriter.WriteAsync(
                _labsRoot() ?? string.Empty, Acme.Notes.Domain.NoteNodeType.McpServer, config.Name, json));
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

    private static List<string> ParseArgs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    [RelayCommand]
    private void Cancel() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
