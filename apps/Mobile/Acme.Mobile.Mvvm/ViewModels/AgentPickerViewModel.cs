using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.Notes.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// Agent 选择页 VM：「＋新对话」入口。列出 <see cref="ICustomAgentCatalog"/> 全部 agent，
/// tap 一个 → 直接进 <c>AgentChatDetailPage</c>（仅 agentName，无 sessionId → 首次发送时 lazy-create）。
/// 镜像 <see cref="ContactsViewModel.LoadAiAgentsAsync"/> 取名册。
/// </summary>
public partial class AgentPickerViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentCatalog? _agentCatalog;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<AgentPickItem> Agents { get; } = new();

    /// <summary>tap agent → 导航到新 Agent 聊天（payload = agentName）。</summary>
    public event EventHandler<string>? NavigateToAgentChatRequested;

    public AgentPickerViewModel(ICustomAgentCatalog? agentCatalog = null)
    {
        _agentCatalog = agentCatalog;
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

    /// <summary>tap agent → 触发导航事件（页 code-behind 跳 AgentChatDetailPage?agentName=...）。</summary>
    [RelayCommand]
    public Task PickAgentAsync(string agentName)
    {
        NavigateToAgentChatRequested?.Invoke(this, agentName);
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
