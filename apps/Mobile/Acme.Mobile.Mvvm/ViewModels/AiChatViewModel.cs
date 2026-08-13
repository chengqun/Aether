using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.AppAuth.Domain;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 「AI」tab 的列表 VM：只承载 AI agent 会话（<see cref="Conversation.Type"/> 3/4），
/// 与社交聊天（<see cref="ChatViewModel"/>）分离。AI 会话以 <see cref="IConversationStore"/>
/// （conversations.db）为权威源，不走 SignalR，故每次激活重载摘要拿最新 last-message。
/// 新会话经 <see cref="NewConversationEvent"/>（<see cref="IEventBusService"/>）跨 VM 接收。
/// </summary>
public partial class AiChatViewModel : NavigableViewModelBase
{
    private readonly IConversationStore? _conversationStore;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IEventBusService? _eventBusService;
    private readonly ICurrentUser? _currentUser;
    private Guid? _cachedUserId;
    private bool _isSubscribed = false;

    [ObservableProperty]
    private string _title = "AI";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private ObservableConversation? _selectedConversation;

    public ObservableCollection<ObservableConversation> Conversations { get; } = new();

    /// <summary>导航到 Agent 聊天详情页事件（tap 既有 AI 会话）。</summary>
    public event EventHandler<AgentChatDetailNavigationEventArgs>? NavigateToAgentChatDetailRequested;

    /// <summary>导航到 agent 选择页事件（「＋新对话」）。</summary>
    public event EventHandler? NavigateToAgentPickerRequested;

    public AiChatViewModel(
        IConversationStore? conversationStore = null,
        ICustomAgentCatalog? agentCatalog = null,
        IEventBusService? eventBusService = null,
        ICurrentUser? currentUser = null,
        IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _conversationStore = conversationStore;
        _agentCatalog = agentCatalog;
        _eventBusService = eventBusService;
        _currentUser = currentUser;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        if (_cachedUserId.HasValue)
            return _cachedUserId.Value;

        if (_currentUser != null)
        {
            var raw = await _currentUser.GetCurrentUserIdAsync();
            if (Guid.TryParse(raw, out var uid))
            {
                _cachedUserId = uid;
                return uid;
            }
        }

        _cachedUserId = new Guid("11111111-1111-1111-1111-111111111111");
        return _cachedUserId.Value;
    }

    public override async Task OnActivatedAsync()
    {
        SubscribeEvents();
        // 每次激活重载：AI 会话无实时事件，靠激活时从 conversations.db 拿最新 last-message。
        await LoadConversationsAsync();
    }

    public override Task OnDeactivatedAsync()
    {
        // 不取消 EventBus 订阅——保持 NewConversationEvent 实时接收（同 ChatViewModel 模式）。
        return Task.CompletedTask;
    }

    private void SubscribeEvents()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;
        _eventBusService?.Subscribe<NewConversationEvent>(OnNewConversation);
    }

    /// <summary>从 <see cref="IConversationStore"/> 加载 AI 会话摘要。镜像 ChatViewModel.LoadAgentConversationsAsync。</summary>
    private async Task LoadConversationsAsync()
    {
        if (_conversationStore == null)
        {
            StatusMessage = "暂无 AI 对话";
            return;
        }

        try
        {
            var userId = await GetCurrentUserIdAsync();
            var summaries = await _conversationStore.GetSummariesAsync(userId.ToString());
            var agentKindMap = await BuildAgentKindMapAsync();

            _uiThreadDispatcher?.Post(() =>
            {
                Conversations.Clear();
                foreach (var s in summaries)
                {
                    var agentName = string.IsNullOrEmpty(s.AgentName) ? s.Title : s.AgentName;
                    var conv = new Conversation
                    {
                        Id = s.ConversationId,
                        Type = 3,
                        Name = agentName,
                        AgentName = agentName,
                        Kind = (int?)agentKindMap.GetValueOrDefault(agentName),
                        LastMessage = s.LastMessagePreview,
                        LastMessageTime = s.UpdatedAt.LocalDateTime,
                    };
                    Conversations.Add(new ObservableConversation(conv));
                }
                SortConversations();
                StatusMessage = Conversations.Count == 0 ? "暂无 AI 对话" : string.Empty;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatViewModel] 加载 AI 会话失败: {ex.Message}");
        }
    }

    /// <summary>构建 Agent 名称 → Kind 映射（基于 ICustomAgentCatalog 的 NodeType）。</summary>
    private async Task<Dictionary<string, AgentKind>> BuildAgentKindMapAsync()
    {
        var map = new Dictionary<string, AgentKind>();
        try
        {
            if (_agentCatalog != null)
            {
                var agents = await _agentCatalog.ListAsync();
                foreach (var a in agents)
                {
                    map[a.Name] = a.NodeType switch
                    {
                        NoteNodeType.Workflow => AgentKind.Workflow,
                        NoteNodeType.Rule or NoteNodeType.CodeRule => AgentKind.RuleBased,
                        _ => AgentKind.Agent,
                    };
                }
            }
        }
        catch { /* 获取 Agent 信息失败时返回空映射 */ }
        return map;
    }

    /// <summary>新 AI 会话事件：只接 Type 3/4，upsert 到列表。镜像 ChatViewModel.OnNewConversation。</summary>
    private async void OnNewConversation(NewConversationEvent @event)
    {
        var conv = @event.Conversation;
        if (conv.Type != 3 && conv.Type != 4) return;

        if (conv.Kind == null && !string.IsNullOrEmpty(conv.AgentName))
        {
            var agentKindMap = await BuildAgentKindMapAsync();
            conv.Kind = (int?)agentKindMap.GetValueOrDefault(conv.AgentName);
        }

        _uiThreadDispatcher?.Post(() =>
        {
            var existing = Conversations.FirstOrDefault(c => c.Id == conv.Id);
            if (existing == null)
            {
                Conversations.Add(new ObservableConversation(conv));
                SortConversations();
                if (StatusMessage == "暂无 AI 对话")
                    StatusMessage = string.Empty;
            }
        });
    }

    /// <summary>排序：按 LastMessageTime 降序，仅顺序变化才重建（减闪烁）。</summary>
    private void SortConversations()
    {
        var desired = Conversations.OrderByDescending(c => c.LastMessageTime).ToList();
        bool needsReorder = false;
        for (int i = 0; i < desired.Count; i++)
        {
            if (Conversations[i].Id != desired[i].Id)
            {
                needsReorder = true;
                break;
            }
        }
        if (!needsReorder) return;
        Conversations.Clear();
        foreach (var conv in desired) Conversations.Add(conv);
    }

    [RelayCommand]
    public async Task RefreshConversationsAsync()
    {
        IsLoading = true;
        try
        {
            await LoadConversationsAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task NavigateToChatDetailAsync()
    {
        if (SelectedConversation == null) return;

        var conv = SelectedConversation;
        SelectedConversation = null;

        if (conv.Type == 4)
        {
            // 从 Context 解析 stockId 和 stockName（Context = "stock:{stockId}:{stockName}"）
            var stockName = "股票";
            var stockId = conv.Id.ToString();
            if (!string.IsNullOrEmpty(conv.Context) && conv.Context.StartsWith("stock:"))
            {
                var parts = conv.Context.Split(':', 3);
                if (parts.Length >= 2) stockId = parts[1];
                if (parts.Length >= 3) stockName = parts[2];
            }
            NavigateToAgentChatDetailRequested?.Invoke(this, new AgentChatDetailNavigationEventArgs
            {
                SessionId = conv.Id,
                StockId = stockId,
                StockName = stockName,
                AgentName = conv.AgentName ?? "指标分析Agent"
            });
            return;
        }

        // Type 3
        NavigateToAgentChatDetailRequested?.Invoke(this, new AgentChatDetailNavigationEventArgs
        {
            SessionId = conv.Id,
            AgentName = conv.Name ?? "AI 助手"
        });
    }

    /// <summary>「＋新对话」→ 打开 agent 选择页。</summary>
    [RelayCommand]
    public Task NewConversationAsync()
    {
        NavigateToAgentPickerRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task DeleteConversationAsync(Guid conversationId)
    {
        var conv = Conversations.FirstOrDefault(c => c.Id == conversationId);
        if (conv == null) return;

        try
        {
            IsLoading = true;
            if (_conversationStore != null)
                await _conversationStore.DeleteAsync(conversationId);
            Conversations.Remove(conv);
            StatusMessage = Conversations.Count == 0 ? "暂无 AI 对话" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
