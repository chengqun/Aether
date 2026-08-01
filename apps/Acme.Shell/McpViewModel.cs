using System.Collections.ObjectModel;
using Aether.AI.Hosting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// MCP 工具服务器管理 ViewModel（Experts 页 MCP tab）：列出已配置 server + 连接状态 + 每个的工具，
/// 增删改配置（写回 mcp-servers/*.json）+ 重连。配置经 <see cref="IMcpManagementService"/>；编辑表单用
/// <see cref="McpServerEditViewModel"/>（由 View 以模态窗口弹出）。
/// </summary>
public partial class McpViewModel : ObservableObject
{
    private readonly IMcpManagementService _mgmt;
    private readonly IUiDispatcher _dispatcher;

    public ObservableCollection<McpServerStatus> Servers { get; } = new();

    [ObservableProperty] private bool _isLoading;

    public McpViewModel(IMcpManagementService mgmt, IUiDispatcher dispatcher)
    {
        _mgmt = mgmt;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            var servers = await _mgmt.GetServersAsync();
            await _dispatcher.InvokeAsync(() =>
            {
                Servers.Clear();
                foreach (var s in servers) Servers.Add(s);
            });
        }
        catch { /* 加载失败不阻断 */ }
        finally { IsLoading = false; }
    }

    /// <summary>确保 MCP 已连接（等连接完成）+ 刷新列表。编辑器打开前调用，保证 MCP 区有最新工具。</summary>
    public async Task EnsureLoadedAsync()
    {
        try { await _mgmt.EnsureStartedAsync(); } catch { /* 连接失败不阻断，仍刷新状态 */ }
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    /// <summary>构造编辑表单 VM（新建传 null，编辑传现有 status）。</summary>
    public McpServerEditViewModel CreateEditViewModel(McpServerStatus? existing)
        => new(existing?.Config);

    /// <summary>提交编辑（保存配置 + 重连 + 刷新列表）。由 View 在编辑窗口保存后调用。</summary>
    public async Task CommitEditAsync(McpServerConfig config)
    {
        await _mgmt.SaveServerAsync(config);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(McpServerStatus? status)
    {
        if (status is null) return;
        await _mgmt.DeleteServerAsync(status.Name);
        await LoadAsync();
    }
}
