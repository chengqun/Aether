using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
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
using Acme.DeviceManagement.Domain;
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
    private readonly IDeviceService? _deviceService;
    private Guid? _cachedUserId;
    private bool _isSubscribed = false;
    private bool _isBootstrapped = false;
    private bool _isLlmLoaded = false;
    private bool _isAgentListLoaded = false;
    /// <summary>桌面设备标识（32 位 DeviceId 字符串，查一次缓存；无桌面设备则为 null → 入口隐藏）。</summary>
    private string? _desktopDeviceId;
    private bool _desktopDeviceResolved = false;
    /// <summary>可用 agent 名册（chip ActionSheet 渲染 + 新对话默认 agent 回退）。null=未加载。</summary>
    private List<string>? _agentNames;
    /// <summary>当前 surface 活动会话 Id（抽屉切换时记录；picker 新对话为 <see cref="Guid.Empty"/> 哨兵，
    /// 待 <see cref="OnNewConversation"/> 回填）。用于删除时判断是否回到空态。</summary>
    private Guid _activeConversationId = Guid.Empty;

    /// <summary>程序化填充下拉/恢复索引时置位，抑制 <see cref="OnSelectedAgentIndexChanged"/>/<see cref="OnSelectedLlmIndexChanged"/>
    /// 的副作用（避免加载期误触发「开新对话」/「切 LLM」）。</summary>
    private bool _syncingPicker;

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

    /// <summary>可用 agent 名单（供页面 agent chip ActionSheet 渲染；<c>null</c>=未加载/不可用）。</summary>
    public IReadOnlyList<string>? AgentNames => _agentNames;

    /// <summary>agent 下拉选项（Picker ItemsSource，加载后填充；与 <see cref="SelectedAgentIndex"/> 配套）。</summary>
    public ObservableCollection<string> AgentOptions { get; } = new();

    /// <summary>agent 下拉选中索引（-1=未选）。变更即用该 agent 开新对话（对齐桌面 ComboBox 选目标）。</summary>
    [ObservableProperty]
    private int _selectedAgentIndex = -1;

    /// <summary>LLM 下拉选项（首项「默认」，其余为各配置名；与 <see cref="SelectedLlmIndex"/> 配套）。</summary>
    public ObservableCollection<string> LlmOptions { get; } = new();

    /// <summary>LLM 下拉选中索引（0=默认回退平台默认；1..n=配置名）。变更调 <see cref="SetLlm"/>。</summary>
    [ObservableProperty]
    private int _selectedLlmIndex = -1;

    /// <summary>当前 LLM 显示名（"默认"=未选/用平台默认；与桌面端一致的全局默认语义，非按会话）。</summary>
    [ObservableProperty]
    private string _currentLlmName = "默认";

    /// <summary>导航请求（Mvvm 库不引用 MAUI Shell，由页面 code-behind 订阅执行 <c>Shell.Current.GoToAsync</c>）。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>是否显示「桌面助理」入口（有绑定的 desktop 设备则为 true）。加载后由 <see cref="ResolveDesktopDeviceAsync"/> 置位。</summary>
    [ObservableProperty]
    private bool _isDesktopAssistantVisible;

    /// <summary>条目级「桌面助理」命令：导航进桌面助手页（顶部「选择电脑」下拉，多台 desktop 设备各自独立会话）。</summary>
    [RelayCommand]
    private async Task OpenDesktopAssistantAsync()
    {
        // 有 desktop 设备才显示入口（IsDesktopAssistantVisible 已门控），此处再次确认避免空入口跳转。
        if (string.IsNullOrEmpty(_desktopDeviceId))
        {
            await ResolveDesktopDeviceAsync();
            if (string.IsNullOrEmpty(_desktopDeviceId))
            {
                _uiThreadDispatcher?.Post(() => StatusMessage = "未找到桌面设备，请先在「我 → 设备管理」绑定电脑");
                return;
            }
        }
        // 无参跳转：DesktopAssistantPage 自行经 IDeviceService 加载 desktop 设备列表并默认打开第一台。
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("DesktopAssistantPage"));
    }

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
        IAppSettings? appSettings = null,
        IDeviceService? deviceService = null)
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
        _deviceService = deviceService;
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
        // 首次激活加载 LLM 列表 + 恢复持久化的选择（写回 holder 让聊天客户端工厂用上）。
        if (!_isLlmLoaded)
        {
            _isLlmLoaded = true;
            await LoadLlmConfigsAsync();
        }
        // 首次激活加载 agent 名单（agent chip ActionSheet + 新对话默认 agent 回退用）。
        if (!_isAgentListLoaded)
        {
            _isAgentListLoaded = true;
            await LoadAgentNamesAsync();
        }

        // 解析桌面设备（决定「桌面助理」入口显隐；无 IDeviceService / 无 desktop 设备则隐藏入口）。
        if (!_desktopDeviceResolved)
        {
            _desktopDeviceResolved = true;
            await ResolveDesktopDeviceAsync();
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

            // 填充 LLM 下拉（首项「默认」+ 各配置名）
            _syncingPicker = true;
            LlmOptions.Clear();
            LlmOptions.Add("默认");
            foreach (var c in LlmConfigs ?? new List<LlmConfigDto>()) LlmOptions.Add(c.Name);

            // 恢复上次选择：持久化 id 命中列表则写 holder + 刷新显示名，否则回退默认。
            var savedId = _appSettings?.GetString("selectedLlmId", string.Empty);
            int idx = 0;
            if (!string.IsNullOrEmpty(savedId) && Guid.TryParse(savedId, out var id))
            {
                var matched = LlmConfigs?.FirstOrDefault(c => c.Id == id);
                if (matched != null)
                {
                    if (_llmHolder != null) _llmHolder.LlmConfigId = savedId;
                    CurrentLlmName = matched.Name;
                    idx = (LlmConfigs ?? new List<LlmConfigDto>()).ToList().IndexOf(matched) + 1;
                }
            }
            else
            {
                CurrentLlmName = "默认";
            }
            SelectedLlmIndex = idx;
            _syncingPicker = false;
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
        // Invalidate 当前 agent 缓存，让下一轮用新 LLM 重建（聊天客户端工厂读 holder）。
        var agentId = ChatSurface.AgentId;
        if (agentId != Guid.Empty) _agentRunner?.Invalidate(agentId);
    }

    /// <summary>agent 下拉变更：用所选 agent 开新对话（对齐桌面 ComboBox 选目标即切）。
    /// 加载/恢复期置 <see cref="_syncingPicker"/> 短路，避免误触发。</summary>
    partial void OnSelectedAgentIndexChanged(int value)
    {
        if (_syncingPicker) return;
        if (value < 0 || value >= AgentOptions.Count) return;
        var name = AgentOptions[value];
        if (string.Equals(name, ChatSurface.AgentName, StringComparison.Ordinal) && ChatSurface.Messages.Count > 0) return;
        _ = StartNewConversationWithAgentAsync(name);
    }

    /// <summary>LLM 下拉变更：索引 0=默认回退；否则取对应 <see cref="LlmConfigDto"/> 调 <see cref="SetLlm"/>。</summary>
    partial void OnSelectedLlmIndexChanged(int value)
    {
        if (_syncingPicker) return;
        if (value < 0 || value >= LlmOptions.Count) return;
        if (value == 0)
        {
            if (!string.IsNullOrEmpty(_appSettings?.GetString("selectedLlmId", string.Empty)))
                SetLlm(null);
            return;
        }
        var configs = LlmConfigs ?? new List<LlmConfigDto>();
        if (value - 1 < configs.Count)
            SetLlm(configs[value - 1]);
    }

    /// <summary>查询并缓存 desktop 设备的 DeviceId 字符串（用于「桌面助理」入口显隐）。无 IDeviceService / 无 desktop 设备返回 null。
    /// desktop 判定：DeviceType 含 desktop/windows/macos（兼容桌面端上报的不同类型串）。</summary>
    private async Task<string?> ResolveDesktopDeviceAsync()
    {
        if (_desktopDeviceId != null) return _desktopDeviceId;

        if (_deviceService == null)
        {
            _desktopDeviceId = null;
            return null;
        }

        try
        {
            var devices = await Task.Run(async () => await _deviceService.GetMyDevicesAsync());
            var desktop = devices?.FirstOrDefault(d =>
            {
                var t = d.DeviceType ?? string.Empty;
                return t.Contains("desktop", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("windows", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("macos", StringComparison.OrdinalIgnoreCase);
            });

            _desktopDeviceId = !string.IsNullOrWhiteSpace(desktop?.DeviceId) ? desktop.DeviceId : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatViewModel] 解析桌面设备失败: {ex.Message}");
            _desktopDeviceId = null;
        }

        IsDesktopAssistantVisible = !string.IsNullOrEmpty(_desktopDeviceId);
        return _desktopDeviceId;
    }

    /// <summary>从 <see cref="ICustomAgentCatalog"/> 载入 agent 名单（agent 下拉 + 新对话默认 agent 回退）。</summary>
    private async Task LoadAgentNamesAsync()
    {
        if (_agentCatalog == null) return;
        try
        {
            var agents = await _agentCatalog.ListAsync();
            _agentNames = agents.Select(a => a.Name).ToList();

            // 填充 agent 下拉 + 同步当前选中索引（当前 surface 的 agent 名优先，否则首个）。
            _syncingPicker = true;
            AgentOptions.Clear();
            foreach (var n in _agentNames) AgentOptions.Add(n);
            var current = ChatSurface.AgentName;
            var idx = _agentNames.IndexOf(current);
            if (idx < 0 && _agentNames.Count > 0) idx = 0;
            SelectedAgentIndex = idx;
            _syncingPicker = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AiChatViewModel] 加载 agent 名单失败: {ex.Message}");
        }
    }

    private void SubscribeEvents()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;
        _eventBusService?.Subscribe<NewConversationEvent>(OnNewConversation);
        _eventBusService?.Subscribe<AgentPickedEvent>(OnAgentPicked);
        _eventBusService?.Subscribe<ConversationTitleUpdatedEvent>(OnConversationTitleUpdated);
        _eventBusService?.Subscribe<LlmConfigsChangedEvent>(OnLlmConfigsChanged);
    }

    /// <summary>LLM 配置在设置 tab 变更后，重载下拉列表（保留当前选择：命中列表则保持，否则回退默认）。</summary>
    private void OnLlmConfigsChanged(LlmConfigsChangedEvent @event)
    {
        // 属性设置（CurrentLlmName）需在 UI 线程；LoadLlmConfigsAsync 内部 Task.Run 读缓存。
        _uiThreadDispatcher?.Post(async () => await LoadLlmConfigsAsync());
    }

    /// <summary>以当前 agent（不在名册则回退首个）在 surface 内开新对话；无可用 agent 时不动作并返回 false。</summary>
    private async Task<bool> StartNewConversationFromCurrentAsync()
    {
        var agent = ChatSurface.AgentName;
        if (string.IsNullOrEmpty(agent) || (_agentNames != null && !_agentNames.Contains(agent)))
            agent = _agentNames?.FirstOrDefault();
        if (string.IsNullOrEmpty(agent)) return false;
        await StartNewConversationWithAgentAsync(agent);
        return true;
    }

    /// <summary>首次激活：有历史则恢复最近一条；无则直接进入新对话（底部已有 agent+LLM 选择条，无需空态页）。</summary>
    private async Task BootstrapAsync()
    {
        var recent = Conversations.OrderByDescending(c => c.LastMessageTime).FirstOrDefault();
        if (recent != null)
        {
            await SwitchToConversationAsync(recent);
        }
        else
        {
            // 无历史会话：直接进入新对话面板（agent chip + LLM chip + 输入栏已就位）。
            // 有可用 agent 则以其开新会话；无则显示空面板，用户经 agent chip 选择后再发消息。
            if (!await StartNewConversationFromCurrentAsync())
                HasActiveChat = true;
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
                // AgentName = 真实 agent 名（kind 映射 + 切换会话时锁定 agent）；显示名优先 AI 生成的标题，
                // 其次 agent 名（lazy-create 时标题先用 agent 名占位，LLM 生成完真正标题后经
                // ConversationTitleUpdatedEvent 就地刷新，故此处仅决定初次载入的显示名）。
                var agentName = string.IsNullOrEmpty(s.AgentName) ? null : s.AgentName;
                var displayName = !string.IsNullOrEmpty(s.Title) ? s.Title
                                  : (!string.IsNullOrEmpty(agentName) ? agentName : "AI 对话");
                var conv = new Conversation
                {
                    Id = s.ConversationId,
                    Type = 3,
                    Name = displayName,
                    AgentName = agentName ?? displayName,
                    Kind = (int?)agentKindMap.GetValueOrDefault(agentName ?? string.Empty),
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
        SyncAgentPicker(conv.AgentName ?? conv.Name);
    }

    /// <summary>抽屉历史项 tap（页面 code-behind SelectionChanged 调）。</summary>
    public async Task SelectHistoryAsync(ObservableConversation? conv)
    {
        if (conv == null) return;
        SelectedHistory = null; // 清除选择，便于再次点同一项
        await SwitchToConversationAsync(conv);
        IsDrawerOpen = false;
    }

    /// <summary>新对话：直接用当前 agent 开新会话，不弹 agent 选择页（agent 经 chip ActionSheet 切换）。
    /// 当前 agent 名不在名册（如默认占位名）时回退到名册首个 agent；无可用 agent 则不动作。</summary>
    [RelayCommand]
    private async Task NewConversation()
    {
        IsDrawerOpen = false;
        await StartNewConversationFromCurrentAsync();
    }

    /// <summary>用指定 agent 在 surface 内开新对话（首条消息 lazy-create）。
    /// agent chip ActionSheet 选定 / picker AgentPickedEvent / 新对话默认 agent 均经此入口。</summary>
    public async Task StartNewConversationWithAgentAsync(string agentName)
    {
        // 复用同一 agent 开新对话时，currentKey 不变→SetParameters 不重置；强制视为切换以清空消息/标题。
        ChatSurface.ForceNewConversation();
        ChatSurface.SetParameters(new Dictionary<string, object>
        {
            ["agentName"] = agentName,
        });
        _activeConversationId = Guid.Empty; // 新会话，待首条消息 lazy-create 后 OnNewConversation 回填
        ChatSurface.IsVoiceMode = false;
        await ChatSurface.OnActivatedAsync();
        HasActiveChat = true;
        IsDrawerOpen = false;
        SyncAgentPicker(agentName);
    }

    /// <summary>把 agent 下拉索引同步到指定 agent 名（置 <see cref="_syncingPicker"/> 抑制副作用）。
    /// 抽屉切会话 / 开新对话后调用，保证 Picker 显示与当前 surface agent 一致。</summary>
    private void SyncAgentPicker(string? agentName)
    {
        if (string.IsNullOrEmpty(agentName)) return;
        var idx = AgentOptions.IndexOf(agentName);
        if (idx < 0) return;
        _syncingPicker = true;
        SelectedAgentIndex = idx;
        _syncingPicker = false;
    }

    [RelayCommand]
    private void OpenDrawer() => IsDrawerOpen = true;

    /// <summary>返回通讯录 tab（AI 页全屏无 tab bar，经绝对路由切回 ContactsPage，tab bar 恢复）。
    /// AI 中间 tab 右滑返回回「通讯录」，与前一个 tab 连贯。</summary>
    [RelayCommand]
    private void CloseAi()
    {
        IsDrawerOpen = false;
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("ContactsPage", isAbsolute: true));
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
            // 删的是当前活动会话 → 直接开新对话（不回空态，底部选择条始终可用）
            if (_activeConversationId == conversationId)
            {
                _activeConversationId = Guid.Empty;
                if (!await StartNewConversationFromCurrentAsync())
                    HasActiveChat = true;
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

    /// <summary>picker 选定 agent：在 surface 内开新对话（首条消息 lazy-create）。
    /// 经 <see cref="StartNewConversationWithAgentAsync"/> 与 agent chip ActionSheet 路径合一。</summary>
    private async void OnAgentPicked(AgentPickedEvent @event)
        => await StartNewConversationWithAgentAsync(@event.AgentName);

    /// <summary>AI 会话标题生成完成（首轮 fire-and-forget）：就地刷新抽屉列表项的显示名，
    /// 无需重载历史列表（lazy-create 时占位为 agent 名，此处替换为真正的标题）。</summary>
    private void OnConversationTitleUpdated(ConversationTitleUpdatedEvent @event)
    {
        _uiThreadDispatcher?.Post(() =>
        {
            var existing = Conversations.FirstOrDefault(c => c.Id == @event.ConversationId);
            existing?.UpdateTitle(@event.Title);
        });
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
