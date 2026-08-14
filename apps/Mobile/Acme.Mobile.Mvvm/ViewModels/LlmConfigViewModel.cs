using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// LLM 配置管理 VM：列出当前用户可见 LLM 配置（自建 + 系统级）+ 添加/编辑（纯本地）+ 从服务端手动同步。
/// MAUI 本地优先：保存仅写本地 SQLite（默认互斥）；同步按钮从服务端拉取并立即应用到运行时（手动触发）。
/// Aether 的 <see cref="ILlmConfigService"/> 内部经 <c>ICurrentUser</c> 解析用户，无需 VM 传 userId。
/// 保存/同步后发布 <see cref="LlmConfigsChangedEvent"/>，让 AI 聊天下拉框即时刷新（无需切 tab 回切）。
/// </summary>
public partial class LlmConfigViewModel : ObservableObject
{
    private readonly ILlmConfigService _llmConfigService;
    private readonly IAgentConfigInitializer? _agentConfigInitializer;
    private readonly IEventBusService? _eventBusService;

    public ObservableCollection<LlmConfigDto> Llms { get; } = new();

    [ObservableProperty] private string _status = "加载中...";
    [ObservableProperty] private bool _isLoading;

    public LlmConfigViewModel(ILlmConfigService llmConfigService,
                              IAgentConfigInitializer? agentConfigInitializer = null,
                              IEventBusService? eventBusService = null)
    {
        _llmConfigService = llmConfigService;
        _agentConfigInitializer = agentConfigInitializer;
        _eventBusService = eventBusService;
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var list = await Task.Run(() => _llmConfigService.GetCachedAsync());
            Llms.Clear();
            if (list != null) foreach (var l in list) Llms.Add(l);
            Status = Llms.Count > 0 ? $"共 {Llms.Count} 个 LLM" : "暂无 LLM,点 + 添加或同步";
        }
        catch (Exception ex) { Status = $"加载失败: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>从服务端手动同步 LLM 配置并立即应用到运行时（手动触发，非登录/后台自动）</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        IsLoading = true;
        Status = "同步中...";
        try
        {
            // 拉取并替换本地缓存，同步后新 LLM 立即对 Agent 生效，无需重登
            await Task.Run(() => _llmConfigService.SyncFromServerAsync());
            if (_agentConfigInitializer != null)
                await _agentConfigInitializer.RefreshLlmConfigAsync();
            await LoadAsync();
            _eventBusService?.Publish(new LlmConfigsChangedEvent("synced"));
            Status = "同步完成";
        }
        catch (Exception ex) { Status = $"同步失败: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>保存（新建/编辑）LLM 配置：纯本地写缓存（默认互斥），不推服务端</summary>
    [RelayCommand]
    private async Task SaveLlmAsync(LlmConfigDto dto)
    {
        try
        {
            await Task.Run(() => _llmConfigService.SetConfigAsync(dto));
            await LoadAsync();
            // 保存后若影响默认 LLM，重新应用一次让运行时生效
            if (_agentConfigInitializer != null)
                await _agentConfigInitializer.RefreshLlmConfigAsync();
            _eventBusService?.Publish(new LlmConfigsChangedEvent("saved"));
            Status = $"已保存 {dto.Name}";
        }
        catch (Exception ex) { Status = $"保存失败: {ex.Message}"; }
    }
}
