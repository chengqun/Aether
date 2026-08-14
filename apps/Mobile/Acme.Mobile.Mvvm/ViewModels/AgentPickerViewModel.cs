using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// Agent 选择页 VM：「＋新对话」入口。列出 <see cref="ICustomAgentCatalog"/> 全部 agent，
/// tap 一个 → 发布 <see cref="AgentPickedEvent"/>（经 <see cref="IEventBusService"/>）通知 AI tab
/// 在内嵌聊天面板内开新对话（首条消息 lazy-create），picker 页自行 pop 回 AI tab。
/// 取名册逻辑独立于此（agent 对话现已从通讯录独立为 AI tab）。
/// </summary>
public partial class AgentPickerViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IEventBusService? _eventBusService;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<AgentPickItem> Agents { get; } = new();

    public AgentPickerViewModel(ICustomAgentCatalog? agentCatalog = null, IEventBusService? eventBusService = null)
    {
        _agentCatalog = agentCatalog;
        _eventBusService = eventBusService;
    }

    public override async Task OnActivatedAsync()
    {
        await LoadAgentsAsync();
    }

    private async Task LoadAgentsAsync()
    {
        if (_agentCatalog == null) return;
        IsLoading = true;
        try
        {
            var agents = await _agentCatalog.ListAsync();
            Agents.Clear();
            foreach (var a in agents)
            {
                Agents.Add(new AgentPickItem
                {
                    Name = a.Name,
                    Initial = string.IsNullOrEmpty(a.Name) ? "?" : a.Name[..1],
                    KindLabel = a.NodeType switch
                    {
                        NoteNodeType.Workflow => "工作流",
                        NoteNodeType.Rule or NoteNodeType.CodeRule => "规则",
                        _ => "智能体",
                    },
                });
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentPickerViewModel] 加载 agent 失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>tap agent → 发布 <see cref="AgentPickedEvent"/>（AI tab 订阅后在 surface 内开新对话）；页 code-behind 随后 pop 回 AI tab。</summary>
    [RelayCommand]
    public Task PickAgentAsync(string agentName)
    {
        _eventBusService?.Publish(new AgentPickedEvent(agentName));
        return Task.CompletedTask;
    }
}

/// <summary>Agent 选择列表项。</summary>
public partial class AgentPickItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _initial = "?";

    [ObservableProperty]
    private string _kindLabel = "智能体";
}
