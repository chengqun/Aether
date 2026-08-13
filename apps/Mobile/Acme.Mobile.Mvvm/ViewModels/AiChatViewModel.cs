using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.AppAuth.Domain;
using Acme.ChatSocial.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;
using Acme.Notes.Domain;
using Aether.AI.Harness;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 「AI」tab 宿主 VM（豆包式）：对话优先 + 左侧抽屉历史。
/// 持有常驻 <see cref="ChatSurface"/>（<see cref="AiChatSurfaceViewModel"/> Singleton），抽屉切换会话时调
/// <see cref="AgentChatDetailViewModel.SetParameters"/> 就地重载，不走 Shell 推送。历史列表来自
/// <see cref="IConversationStore.GetSummariesAsync"/>；新会话经 <see cref="NewConversationEvent"/> 实时入列；
/// picker 选定 agent 经 <see cref="AgentPickedEvent"/> 在 surface 内开新对话。
/// </summary>
public partial class AiChatViewModel : NavigableViewModelBase
{
    private readonly IConversationStore? _conversationStore;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IEventBusService? _eventBusService;
    private readonly ICurrentUser? _currentUser;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly SelectedLlmHolder? _llmHolder;
    private readonly ICustomAgentRunner? _agentRunner;
    private readonly IAppSettings? _appSettings;
    private Guid? _cachedUserId;
    private bool _isSubscribed = false;
    private bool _isBootstrapped = false;
    private bool _isLlmLoaded = false;
    /// <summary>当前 surface 活动会话 Id（抽屉切换时记录；picker 新对话为 <see cref="Guid.Empty"/> 哨兵，
    /// 待 <see cref="OnNewConversation"/> 回填）。用于删除时判断是否回到空态。</summary>
    private Guid _activeConversationId = Guid.Empty;

    /// <summary>内嵌常驻聊天面板 VM（Singleton），页面把 AgentChatSurface.BindingContext 绑到它。</summary>
    public AgentChatDetailViewModel ChatSurface { get; }

    public ObservableCollection<ObservableConversation> Conversations { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>抽屉是否展开（页面 code-behind 监听变化做 TranslationX 动画）。</summary>
    [ObservableProperty]
    private bool _isDrawerOpen;

    /// <summary>是否有活动对话（false→显示空态「选择 AI 助手」，true→显示聊天面板）。</summary>
    [ObservableProperty]
    private bool _hasActiveChat;

    /// <summary>抽屉历史列表选中项。</summary>
    [ObservableProperty]
    private ObservableConversation? _selectedHistory;

    /// <summary>可选 LLM 配置列表（供页面 ActionSheet 渲染；<c>null</c>=未加载/不可用）。</summary>
    public List<LlmConfigDto>? LlmConfigs { get; private set; }

    /// <summary>当前 LLM 显示名（"默认"=未选/用平台默认；与桌面端一致的全局默认语义，非按会话）。</summary>
    [ObservableProperty]
    private string _currentLlmName = "默认";

    /// <summary>导航请求（Mvvm 库不引用 MAUI Shell，由页面 code-behind 订阅执行 <c>Shell.Current.GoToAsync</c>）。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public AiChatViewModel(
        AiChatSurfaceViewModel chatSurface,
        IConversationStore? conversationStore = null,
        ICustomAgentCatalog? agentCatalog = null,
        IEventBusService? eventBusService = null,
        ICurrentUser? currentUser = null,
        IUIThreadDispatcher? uiThreadDispatcher = null,
        ILlmConfigService? llmConfigService = null,
        SelectedLlmHolder? llmHolder = null,
        ICustomAgentRunner? agentRunner = null,
        IAppSettings? appSettings = null)
    {
        ChatSurface = chatSurface;
        _conversationStore = conversationStore;
        _agentCatalog = agentCatalog;
        _eventBusService = eventBusService;
        _currentUser = currentUser;
        _uiThreadDispatcher = uiThreadDispatcher;
        _llmConfigService = llmConfigService;
        _llmHolder = llmHolder;
        _agentRunner = agentRunner;
        _appSettings = appSettings;
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
        await LoadConversationsAsync();
        // 首次激活加载 LLM 列表 + 恢复持久化的选择（写回 holder 让 MobileChatClientFactory 用上）。
        if (!_isLlmLoaded)
        {
            _isLlmLoaded = true;
            await LoadLlmConfigsAsync();
        }

        // 仅首次激活 bootstrap：恢复最近会话或进入空态。后续激活（tab 回切/picker 返回）只刷新历史列表。
        if (!_isBootstrapped)
        {
            _isBootstrapped = true;
            await BootstrapAsync();
        }
    }

    public override async Task OnDeactivatedAsync()
    {
        // 取消在跑流式 + 停后台服务（不清 Messages，回切时保持）。
        await ChatSurface.OnDeactivatedAsync();
    }

    /// <summary>加载 LLM 配置列表并恢复持久化的选择（写回 <see cref="SelectedLlmHolder"/>，与桌面端全局默认语义一致）。</summary>
    private async Task LoadLlmConfigsAsync()
    {
        if (_llmConfigService == null) return;
        try
        {
            LlmConfigs = await Task.Run(() => _llmConfigService.GetCachedAsync());
            // 恢复上次选择：持久化 id 命中列表则写 holder + 刷新显示名，否则回退默认。
            var savedId = _appSettings?.GetString("selectedLlmId", string.Empty);
            if (!string.IsNullOrEmpty(savedId) && Guid.TryParse(savedId, out var id))
            {
                var matched = LlmConfigs?.FirstOrDefault(c => c.Id == id);
                if (matched != null)
                {
                    if (_llmHolder != null) _llmHolder.LlmConfigId = savedId;
                    CurrentLlmName = matched.Name;
                }
            }
            else
            {
                CurrentLlmName = "默认";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatViewModel] 加载 LLM 配置失败: {ex.Message}");
        }
    }

    /// <summary>切换全局默认 LLM（与桌面端一致：写 holder + 持久化 + Invalidate 当前 agent 缓存）。
    /// 传 <c>null</c> = 回退平台默认。</summary>
    public void SetLlm(LlmConfigDto? config)
    {
        var id = config?.Id.ToString() ?? string.Empty;
        if (_llmHolder != null) _llmHolder.LlmConfigId = string.IsNullOrEmpty(id) ? null : id;
        try { _appSettings?.SetString("selectedLlmId", id); } catch { /* 无 store 则跳过持久化 */ }
        CurrentLlmName = config?.Name ?? "默认";
        // Invalidate 当前 agent 缓存，让下一轮用新 LLM 重建（MobileChatClientFactory 读 holder）。
        var agentId = ChatSurface.AgentId;
        if (agentId != Guid.Empty) _agentRunner?.Invalidate(agentId);
    }

    private void SubscribeEvents()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;
        _eventBusService?.Subscribe<NewConversationEvent>(OnNewConversation);
        _eventBusService?.Subscribe<AgentPickedEvent>(OnAgentPicked);
    }

    /// <summary>首次激活：有历史则恢复最近一条，无则空态。</summary>
    private async Task BootstrapAsync()
    {
        var recent = Conversations.OrderByDescending(c => c.LastMessageTime).FirstOrDefault();
        if (recent != null)
        {
            await SwitchToConversationAsync(recent);
        }
        else
        {
            HasActiveChat = false;
        }
    }

    /// <summary>从 <see cref="IConversationStore"/> 重载历史列表（不动 ChatSurface）。
    /// 调用方在 UI 线程（<see cref="OnActivatedAsync"/> 自 <c>OnAppearing</c> / RelayCommand），
    /// <c>await</c> 捕获 UI 同步上下文 → 续体仍在 UI 线程，直接改 <see cref="Conversations"/> 安全，
    /// 且保证随后 <see cref="BootstrapAsync"/> 能读到已填充列表。</summary>
    private async Task LoadConversationsAsync()
    {
        if (_conversationStore == null) return;

        try
        {
            var userId = await GetCurrentUserIdAsync();
            var summaries = await _conversationStore.GetSummariesAsync(userId.ToString());
            var agentKindMap = await BuildAgentKindMapAsync();

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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatViewModel] 加载历史失败: {ex.Message}");
        }
    }

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
        catch { /* 空映射兜底 */ }
        return map;
    }

    /// <summary>切换到指定历史会话（抽屉 tap）：SetParameters 就地重载。</summary>
    public async Task SwitchToConversationAsync(ObservableConversation conv)
    {
        ChatSurface.SetParameters(new Dictionary<string, object>
        {
            ["sessionId"] = conv.Id.ToString(),
            ["agentName"] = conv.AgentName ?? conv.Name ?? "AI 助手",
        });
        _activeConversationId = conv.Id;
        ChatSurface.IsVoiceMode = false;
        await ChatSurface.OnActivatedAsync();
        HasActiveChat = true;
    }

    /// <summary>抽屉历史项 tap（页面 code-behind SelectionChanged 调）。</summary>
    public async Task SelectHistoryAsync(ObservableConversation? conv)
    {
        if (conv == null) return;
        SelectedHistory = null; // 清除选择，便于再次点同一项
        await SwitchToConversationAsync(conv);
        IsDrawerOpen = false;
    }

    /// <summary>新对话：推送 agent 选择页（picker 选定后发 AgentPickedEvent 回来）。</summary>
    [RelayCommand]
    private void NewConversation()
    {
        IsDrawerOpen = false;
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("AgentPickerPage"));
    }

    [RelayCommand]
    private void OpenDrawer() => IsDrawerOpen = true;

    /// <summary>返回聊天 tab（AI 页全屏无 tab bar，经绝对路由切回 ChatPage，tab bar 恢复）。</summary>
    [RelayCommand]
    private void CloseAi()
    {
        IsDrawerOpen = false;
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("ChatPage", isAbsolute: true));
    }

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;

    [RelayCommand]
    public async Task RefreshConversationsAsync()
    {
        IsLoading = true;
        try { await LoadConversationsAsync(); }
        finally { IsLoading = false; }
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
            // 删的是当前活动会话 → 回空态（surface 隐藏，下次切换/新建时 SetParameters 自然重置 Messages）
            if (_activeConversationId == conversationId)
            {
                _activeConversationId = Guid.Empty;
                HasActiveChat = false;
            }
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

    /// <summary>新 AI 会话事件（ChatSurface lazy-create 首条消息后发）：upsert 进历史列表。
    /// 不在此处翻 <see cref="HasActiveChat"/>——picker 路径已由 <see cref="OnAgentPicked"/> 置位，
    /// 推送页创建的会话不应强逼 AI tab 显示。</summary>
    private async void OnNewConversation(NewConversationEvent @event)
    {
        var conv = @event.Conversation;
        if (conv.Type != 3 && conv.Type != 4) return;

        if (conv.Kind == null && !string.IsNullOrEmpty(conv.AgentName))
        {
            var agentKindMap = await BuildAgentKindMapAsync();
            conv.Kind = (int?)agentKindMap.GetValueOrDefault(conv.AgentName);
        }

        // picker 新对话首条消息 lazy-create 落地 → 回填活动会话 Id 哨兵
        if (_activeConversationId == Guid.Empty)
            _activeConversationId = conv.Id;

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

    /// <summary>picker 选定 agent：在 surface 内开新对话（首条消息 lazy-create）。</summary>
    private async void OnAgentPicked(AgentPickedEvent @event)
    {
        ChatSurface.SetParameters(new Dictionary<string, object>
        {
            ["agentName"] = @event.AgentName,
        });
        _activeConversationId = Guid.Empty; // 新会话，待首条消息 lazy-create 后 OnNewConversation 回填
        ChatSurface.IsVoiceMode = false;
        await ChatSurface.OnActivatedAsync();
        HasActiveChat = true;
        IsDrawerOpen = false;
    }

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
}
