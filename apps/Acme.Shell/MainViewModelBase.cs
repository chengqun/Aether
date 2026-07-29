using System.Collections.ObjectModel;
using System.Windows.Input;
using Acme.Agents.AI;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain.Interfaces;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Shell;

/// <summary>
/// 桌面 shell 主 ViewModel 基类：可收起侧边栏 + 导航 + 账户/登录态（Codex/ChatGPT 风）。
/// 租户子类在构造里 Populate <see cref="NavItems"/>。内容区使用 ChatView。
/// </summary>
public abstract partial class MainViewModelBase : ObservableObject, IChatViewModel
{
    private readonly IAppAuthService _auth;
    private readonly ICurrentUser _currentUser;
    private readonly ILoginDialogService _loginDialog;
    private readonly IAppTokenStore _tokenStore;
    private readonly ISettingsDialogService _settingsDialog;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceChatClient _deviceChatClient;
    private readonly ILlmConfigService _llmService;
    private readonly ICustomAgentRunner _agentRunner;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly IPreferencesStore _preferences;
    private readonly IConversationTitleService _titleService;
    private readonly IDeviceAutoConnectService _autoConnect;

    // 当前会话：Guid.Empty = 待新聊天（首次发送才落库）。
    private Guid _currentConversationId = Guid.Empty;
    private bool _isInitializing;

    protected MainViewModelBase(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService, string tenant)
    {
        _auth = auth;
        _currentUser = currentUser;
        _loginDialog = loginDialog;
        _tokenStore = tokenStore;
        _settingsDialog = settingsDialog;
        _serviceProvider = serviceProvider;
        _deviceChatClient = deviceChatClient;
        _llmService = llmService;
        _agentRunner = serviceProvider.GetRequiredService<ICustomAgentRunner>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _conversationStore = serviceProvider.GetRequiredService<IConversationStore>();
        _preferences = serviceProvider.GetRequiredService<IPreferencesStore>();
        _titleService = serviceProvider.GetRequiredService<IConversationTitleService>();
        _autoConnect = serviceProvider.GetRequiredService<IDeviceAutoConnectService>();
        Tenant = tenant;

        Notes = new NotesViewModel(serviceProvider, _dispatcher);
        Automation = new AutomationViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            serviceProvider.GetRequiredService<IAutomationService>(),
            serviceProvider.GetRequiredService<IAutomationScheduler>(),
            _dispatcher);
        Experts = new ExpertsViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            _agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<IAgentMarketplaceService>(),
            _auth,
            serviceProvider.GetService<IConnectorViewModel>());
        Experts.SummonChanged += OnSummonChanged;
        Experts.SummonChanged += async () => await Notes.LoadAsync();

        // 定时任务执行完成（成功/失败）后刷新「最近会话」——因为 executor 把运行落库进了会话存储。
        serviceProvider.GetRequiredService<IAutomationScheduler>().TaskCompleted +=
            _ => _dispatcher.Post(async () => await RefreshConversationsAsync());

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;

        SubscribeToConnectionStateChanges();
    }

    public string Tenant { get; }

    public ISettingsDialogService SettingsDialog => _settingsDialog;

    private void SubscribeToConnectionStateChanges()
    {
        _deviceChatClient.ConnectionStateChanged += status =>
        {
            _dispatcher.Post(() =>
            {
                IsConnected = _deviceChatClient.IsConnected;
                StatusText = IsConnected ? string.Empty : status;
            });
        };

        _deviceChatClient.OnMessageReceived += HandleMessageReceived;
    }

    public void RefreshConnectionState()
    {
        _dispatcher.Post(() =>
        {
            IsConnected = _deviceChatClient.IsConnected;
            StatusText = IsConnected ? "已连接" : "未连接";
        });
    }

    /// <summary>LLM 配置变更后刷新聊天页的模型下拉（设置页新增/编辑/删除后调用）。</summary>
    public async Task RefreshLlmConfigsAsync()
    {
        await _dispatcher.InvokeAsync(async () => await LoadLlmConfigsAsync());
    }

    /// <summary>
    /// 流式运行期间的工具调用/结果回调：在聊天流里插入 tool_call / tool_result 卡片，
    /// 排在正在流式输出的助手消息之前（工具卡片在回答上方），让用户看到思考过程中的工具使用与输入输出。
    /// </summary>
    private async Task OnToolEventAsync(ChatMessageItem assistantMsg, ToolStreamEvent ev)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            var item = new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = ev.Kind == ToolStreamEventKind.Call ? "tool_call" : "tool_result",
                Sender = assistantMsg.Sender,
                ToolName = ev.Name,
                Input = ev.Input,
                Output = ev.Output,
            };
            var idx = Messages.IndexOf(assistantMsg);
            if (idx < 0) idx = Messages.Count;
            Messages.Insert(idx, item);
        });
    }

    private Guid _recentConversationId;

    /// <summary>收到远程 SignalR 消息回调（SignalR 线程）</summary>
    private async Task HandleMessageReceived(DeviceMessage msg)
    {
        _recentConversationId = msg.ConversationId;

        // 远程消息显示在右侧（与本机发送一致，参考 AlphaAgent）
        await _dispatcher.InvokeAsync(() =>
        {
            Messages.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Right,
                ItemType = "text",
                Sender = msg.SenderName,
                Content = msg.Content
            });
        });

        // 选中已召唤的自定义专家时，用该专家流式回复
        if (SelectedAgentItem is ExpertItem expert)
        {
            var assistantMsg = new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = expert.Name,
                Content = string.Empty,
                IsStreaming = true,
            };
            await _dispatcher.InvokeAsync(() => Messages.Add(assistantMsg));
            var fullText = new System.Text.StringBuilder();
            try
            {
                await _agentRunner.RunStreamingAsync(msg.ConversationId, expert.Id, msg.Content,
                    async (token, _) =>
                    {
                        fullText.Append(token);
                        await _dispatcher.InvokeAsync(() => assistantMsg.Content += token);
                    },
                    async (ev, _) => await OnToolEventAsync(assistantMsg, ev));
            }
            catch (Exception ex)
            {
                fullText.Append($"\n\n[运行失败：{ex.Message}]");
                await _dispatcher.InvokeAsync(() =>
                    assistantMsg.Content += $"\n\n[运行失败：{ex.Message}]");
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => assistantMsg.IsStreaming = false);
            }

            // 远程消息触发运行：把专家回复通过 SignalR 发回远程发送方（否则对方收不到 agent 结果）。
            // 回环安全：SignalRDeviceChatClient 用 _recentSentMessageIds 去重，服务端 echo 回本机的消息会被忽略。
            var replyText = fullText.ToString();
            if (!string.IsNullOrWhiteSpace(replyText) && _deviceChatClient.IsConnected)
            {
                try
                {
                    await _deviceChatClient.SendMessageAsync(msg.ConversationId, replyText, "Text");
                }
                catch
                {
                    // 发回失败不影响本地已显示的回复
                }
            }
            return;
        }

        // 未选用专家时本地简单回复
        await _dispatcher.InvokeAsync(() =>
        {
            Messages.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = "Aether",
                Content = $"收到来自 {msg.SenderName} 的消息：{msg.Content}",
                IsStreaming = false
            });
        });
    }

    // —— 聊天消息列表 ——
    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    // —— 最近会话列表（侧边栏）——
    public ObservableCollection<ConversationItem> Conversations { get; } = new();

    /// <summary>是否有最近会话（控制空状态"暂无会话"显示）。</summary>
    [ObservableProperty] private bool _hasConversations;

    /// <summary>最近会话列表是否展开（ChatGPT 风，可收起）。</summary>
    [ObservableProperty] private bool _isRecentExpanded = true;

    public bool IsRecentEmptyVisible => IsRecentExpanded && !HasConversations;

    public string RecentCaret => IsRecentExpanded ? "▼" : "▶";

    partial void OnIsRecentExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRecentEmptyVisible));
        OnPropertyChanged(nameof(RecentCaret));
        _ = _preferences.SetAsync("recentExpanded", value ? "1" : "0");
    }

    partial void OnHasConversationsChanged(bool value) => OnPropertyChanged(nameof(IsRecentEmptyVisible));

    [RelayCommand]
    private void ToggleRecentExpanded() => IsRecentExpanded = !IsRecentExpanded;

    [RelayCommand]
    private void NewChat()
    {
        // 懒创建：仅进入待新聊天态，不在此时落库（避免空会话进列表）。
        _currentConversationId = Guid.Empty;
        Messages.Clear();
        foreach (var c in Conversations) c.IsCurrent = false;
        CurrentPage = "chat";
    }

    [RelayCommand]
    private async Task ResumeConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        try
        {
            var conv = await _conversationStore.GetAsync(item.Id);
            _currentConversationId = item.Id;
            foreach (var c in Conversations) c.IsCurrent = c.Id == item.Id;

            _isInitializing = true;
            // 恢复时把下拉选中切到该会话绑定的专家（抑制变更回调，避免触发持久化/新建）
            if (Guid.TryParse(item.AgentId, out var aid))
                SelectedAgentItem = AgentSelectItems.OfType<ExpertItem>().FirstOrDefault(e => e.Id == aid);
            _isInitializing = false;

            Messages.Clear();
            if (conv is not null)
            {
                foreach (var m in conv.Messages)
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Side = m.Role == Aether.AI.Core.Messages.MessageRole.User ? ChatMessageSide.Right : ChatMessageSide.Left,
                        ItemType = "text",
                        Sender = m.Role == Aether.AI.Core.Messages.MessageRole.User ? "我" : item.AgentName,
                        Content = m.Content,
                        IsStreaming = false,
                    });
                }
            }
            CurrentPage = "chat";
        }
        catch (Exception ex)
        {
            StatusText = $"加载会话失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        try
        {
            await _conversationStore.DeleteAsync(item.Id);
            Conversations.Remove(item);
            if (_currentConversationId == item.Id)
            {
                _currentConversationId = Guid.Empty;
                Messages.Clear();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"删除会话失败：{ex.Message}";
        }
    }

    /// <summary>从存储加载最近会话列表（按 UpdatedAt 倒序）。</summary>
    private async Task RefreshConversationsAsync()
    {
        try
        {
            var summaries = await _conversationStore.GetSummariesAsync(string.Empty);
            await _dispatcher.InvokeAsync(() =>
            {
                Conversations.Clear();
                foreach (var s in summaries)
                {
                    Conversations.Add(new ConversationItem
                    {
                        Id = s.ConversationId,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? "新会话" : s.Title,
                        Preview = s.LastMessagePreview ?? string.Empty,
                        DisplayTime = FormatTime(s.UpdatedAt),
                        AgentId = s.AgentId,
                        AgentName = s.AgentName,
                        IsCurrent = s.ConversationId == _currentConversationId,
                    });
                }
            });
        }
        catch { /* 启动期失败不阻塞 */ }
    }

    private static string FormatTime(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        if (local.Date == DateTime.Today) return local.ToString("HH:mm");
        return local.ToString("MM-dd HH:mm");
    }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _canSendInput = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnected;

    public string ConnectionLabel => IsConnected ? "已连接" : "未连接";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _tokenInfo = string.Empty;

    public ObservableCollection<object> AgentSelectItems { get; } = new();
    public ObservableCollection<LlmConfigDto> LlmSelectItems { get; } = new();

    [ObservableProperty]
    private object? _selectedAgentItem;

    [ObservableProperty]
    private LlmConfigDto? _selectedLlmItem;

    // —— 侧边栏收起 ——
    [ObservableProperty]
    private double _sidebarWidth = 220;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 220 : 48;
    }

    // —— 导航 ——
    [ObservableProperty]
    private string _currentPage = "chat";

    /// <summary>是否是聊天页</summary>
    public bool IsChatPage => CurrentPage == "chat";

    /// <summary>是否是笔记页</summary>
    public bool IsNotesPage => CurrentPage == "notes";

    /// <summary>是否是专家页</summary>
    public bool IsExpertsPage => CurrentPage == "experts";

    /// <summary>是否是自动化页</summary>
    public bool IsAutomationPage => CurrentPage == "automation";

    /// <summary>是否是期货页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsFuturesPage => CurrentPage == "ctp";

    /// <summary>是否是回测/实盘页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsTradingPage => CurrentPage == "backtest";

    /// <summary>是否是占位页（非聊天/笔记/专家/自动化/期货/回测）</summary>
    public bool IsPlaceholderPage => !IsChatPage && !IsNotesPage && !IsExpertsPage && !IsAutomationPage && !IsFuturesPage && !IsTradingPage;

    /// <summary>笔记页 ViewModel</summary>
    public NotesViewModel Notes { get; }

    /// <summary>自动化页 ViewModel</summary>
    public AutomationViewModel Automation { get; }

    /// <summary>期货页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 CTP。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Futures { get; protected set; }

    /// <summary>回测/实盘页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 Trading。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Trading { get; protected set; }

    /// <summary>专家页 ViewModel</summary>
    public ExpertsViewModel Experts { get; }

    /// <summary>导航项集合（租户填充）。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    partial void OnCurrentPageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsNotesPage));
        OnPropertyChanged(nameof(IsExpertsPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsFuturesPage));
        OnPropertyChanged(nameof(IsTradingPage));
        OnPropertyChanged(nameof(IsPlaceholderPage));
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        // “新聊天”导航项（chat）真正开一个新会话，而非仅跳页
        if (pageKey == "chat")
        {
            NewChat();
            foreach (var item in NavItems)
                item.IsCurrent = item.Key == pageKey;
            return;
        }

        CurrentPage = pageKey;
        foreach (var item in NavItems)
            item.IsCurrent = item.Key == pageKey;
    }

    protected void AddNav(string key, string icon, string title)
    {
        NavItems.Add(new NavItem
        {
            Key = key,
            Icon = icon,
            Title = title,
            Command = NavigateToCommand,
        });
    }

    // —— 账户 / 登录态 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private string _userName = "未登录";

    /// <summary>账户头像首字母（未登录显示 ?）。</summary>
    public string UserInitial => IsLoggedIn
        ? (string.IsNullOrEmpty(UserName) ? "?" : UserName[..1])
        : "?";

    /// <summary>启动期初始化登录态：检查已缓存的应用 token，刷新账户显示。</summary>
    public virtual async Task InitializeAsync()
    {
        try
        {
            if (await _auth.IsLoggedInAsync())
            {
                IsLoggedIn = true;
                var username = await _tokenStore.GetUsernameAsync();
                UserName = string.IsNullOrWhiteSpace(username) ? "已登录" : username;
            }
            else
            {
                IsLoggedIn = false;
                UserName = "未登录";
            }
        }
        catch
        {
            IsLoggedIn = false;
            UserName = "未登录";
        }

        IsConnected = _deviceChatClient.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        _isInitializing = true;
        try
        {
            await LoadLlmConfigsAsync();
            await Notes.LoadAsync();
            await Experts.LoadAsync();
            await Automation.LoadAsync();
            await RestoreSummonedAndSelectedAsync();
            await RefreshConversationsAsync();
        }
        finally { _isInitializing = false; }

        // 启动自动化调度器（进程内定时触发；仅 App 打开时生效）。双启动守卫已在调度器内。
        _ = _serviceProvider.GetRequiredService<IAutomationService>()
            .StartSchedulerAsync(CancellationToken.None);

        await TryAutoConnectDeviceAsync();
    }

    /// <summary>从偏好恢复召唤状态与选中专家（跨重启保留），并保证启动时有一个默认选中。</summary>
    private async Task RestoreSummonedAndSelectedAsync()
    {
        try
        {
            // 召唤列表
            var summonedJson = await _preferences.GetAsync("summonedAgentIds");
            if (!string.IsNullOrWhiteSpace(summonedJson))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(summonedJson) ?? Array.Empty<string>();
                var set = new HashSet<string>(ids);
                foreach (var e in Experts.Experts)
                    e.IsSummoned = set.Contains(e.Id.ToString());
            }

            // 无任何召唤专家时，自动召唤第一个，保证下拉非空、有默认 agent
            if (Experts.GetSummonedExperts().Count == 0 && Experts.Experts.Count > 0)
            {
                Experts.Experts[0].IsSummoned = true;
                var ids = Experts.GetSummonedExperts().Select(e => e.Id.ToString()).ToArray();
                await _preferences.SetAsync("summonedAgentIds", System.Text.Json.JsonSerializer.Serialize(ids));
            }

            // 同步填充下拉（此时已在 UI 线程；不依赖后续的 RefreshAgentSelectItems）
            AgentSelectItems.Clear();
            foreach (var e in Experts.GetSummonedExperts())
                AgentSelectItems.Add(e);

            // 选中：已保存的 → 回退到第一个召唤的专家
            var selectedId = await _preferences.GetAsync("selectedAgentId");
            var saved = Guid.TryParse(selectedId, out var aid)
                ? AgentSelectItems.OfType<ExpertItem>().FirstOrDefault(e => e.Id == aid)
                : null;
            SelectedAgentItem = saved ?? AgentSelectItems.OfType<ExpertItem>().FirstOrDefault();

            // 恢复最近会话展开状态
            var expanded = await _preferences.GetAsync("recentExpanded");
            if (expanded == "0") IsRecentExpanded = false;
        }
        catch { /* 偏好恢复失败不阻塞 */ }
    }

    /// <summary>加载 LLM 配置列表到下拉框，并恢复上次选中的 LLM（跨重启保留）。</summary>
    private async Task LoadLlmConfigsAsync()
    {
        try
        {
            var configs = await _llmService.GetCachedAsync();
            LlmSelectItems.Clear();
            if (configs != null)
            {
                foreach (var c in configs)
                    LlmSelectItems.Add(c);

                // 选中：上次保存的 → IsDefault → 第一个
                var savedId = await _preferences.GetAsync("selectedLlmId");
                var saved = Guid.TryParse(savedId, out var lid)
                    ? configs.FirstOrDefault(c => c.Id == lid)
                    : null;
                SelectedLlmItem = saved ?? configs.FirstOrDefault(c => c.IsDefault) ?? configs.FirstOrDefault();
            }
        }
        catch
        {
        }
    }

    /// <summary>选中 LLM 变化时持久化（启动期 _isInitializing 抑制）。</summary>
    partial void OnSelectedLlmItemChanged(LlmConfigDto? value)
    {
        if (_isInitializing) return;
        if (value is not null)
            _ = _preferences.SetAsync("selectedLlmId", value.Id.ToString());
    }

    /// <summary>召唤变化：刷新下拉 + 持久化召唤列表。</summary>
    private async void OnSummonChanged()
    {
        RefreshAgentSelectItems();
        try
        {
            var ids = Experts.GetSummonedExperts().Select(e => e.Id.ToString()).ToArray();
            await _preferences.SetAsync("summonedAgentIds", System.Text.Json.JsonSerializer.Serialize(ids));
        }
        catch { /* 偏好持久化失败不阻塞 */ }
    }

    /// <summary>选中专家变化时持久化（启动期 _isInitializing 抑制）。</summary>
    partial void OnSelectedAgentItemChanged(object? value)
    {
        if (_isInitializing) return;
        if (value is ExpertItem expert)
        {
            _ = _preferences.SetAsync("selectedAgentId", expert.Id.ToString());
        }
    }

    /// <summary>刷新聊天页 Agent 下拉框（已召唤的专家），保留当前选中。</summary>
    private void RefreshAgentSelectItems()
    {
        _dispatcher.Post(() =>
        {
            var current = SelectedAgentItem as ExpertItem;
            AgentSelectItems.Clear();
            foreach (var e in Experts.GetSummonedExperts())
                AgentSelectItems.Add(e);

            // 当前仍在召唤列表 → 保持选中；否则回退第一个（避免清空导致选中丢失）
            SelectedAgentItem = (current is not null && AgentSelectItems.Contains(current))
                ? current
                : AgentSelectItems.OfType<ExpertItem>().FirstOrDefault();
        });
    }

    private async Task TryAutoConnectDeviceAsync()
    {
        try
        {
            await _autoConnect.LoadAndAutoConnectAsync();
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (await _loginDialog.ShowLoginAsync())
            await InitializeAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _auth.LogoutAsync();
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        await _settingsDialog.ShowSettingsAsync();
    }

    [RelayCommand]
    private async Task SendInputAsync()
    {
        if (!CanSendInput || string.IsNullOrWhiteSpace(InputText)) return;

        var content = InputText.Trim();
        InputText = string.Empty;

        // 本地显示用户消息（右侧）
        Messages.Add(new ChatMessageItem
        {
            Side = ChatMessageSide.Right,
            ItemType = "text",
            Sender = "我",
            Content = content
        });

        // 选中已召唤的自定义专家时，本地流式运行（不走 SignalR）
        if (SelectedAgentItem is ExpertItem expert)
        {
            var justCreated = false;
            // 待新聊天 → 首次发送才落库创建会话
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                _currentConversationId = Guid.NewGuid();
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = expert.Id.ToString(),
                    AgentName = expert.Name,
                    Title = content.Length > 20 ? content[..20] + "…" : content,
                };
                conv.Append(Aether.AI.Core.Messages.Message.User(content));
                await _conversationStore.SaveAsync(conv);
                await _dispatcher.InvokeAsync(() =>
                {
                    Conversations.Insert(0, new ConversationItem
                    {
                        Id = conv.ConversationId,
                        Title = conv.Title,
                        Preview = content,
                        DisplayTime = FormatTime(conv.UpdatedAt),
                        AgentId = conv.AgentId,
                        AgentName = conv.AgentName,
                        IsCurrent = true,
                    });
                    foreach (var c in Conversations) c.IsCurrent = c.Id == _currentConversationId;
                });
            }

            var assistantMsg = new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = expert.Name,
                Content = string.Empty,
                IsStreaming = true,
            };
            Messages.Add(assistantMsg);
            var fullText = new System.Text.StringBuilder();
            try
            {
                await _agentRunner.RunStreamingAsync(_currentConversationId, expert.Id, content,
                    async (token, _) =>
                    {
                        fullText.Append(token);
                        await _dispatcher.InvokeAsync(() => assistantMsg.Content += token);
                    },
                    async (ev, _) => await OnToolEventAsync(assistantMsg, ev));
            }
            catch (Exception ex)
            {
                fullText.Append($"\n\n[运行失败：{ex.Message}]");
                await _dispatcher.InvokeAsync(() =>
                    assistantMsg.Content += $"\n\n[运行失败：{ex.Message}]");
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => assistantMsg.IsStreaming = false);
            }

            // 持久化 transcript（user + assistant）并乐观 bump 侧栏项
            await PersistTurnAsync(expert, content, fullText.ToString());

            // 首轮后异步用 LLM 生成会话标题（不阻塞；失败静默）
            if (justCreated)
                _ = GenerateAndApplyTitleAsync(content, fullText.ToString());
            return;
        }

        // 通过 SignalR 发送到服务端（若已连接）
        if (_deviceChatClient.IsConnected)
        {
            try
            {
                var conversationId = _recentConversationId != Guid.Empty
                    ? _recentConversationId
                    : Guid.NewGuid();
                await _deviceChatClient.SendMessageAsync(conversationId, content, "Text");
                _recentConversationId = conversationId;
            }
            catch (Exception ex)
            {
                Messages.Add(new ChatMessageItem
                {
                    Side = ChatMessageSide.Left,
                    ItemType = "text",
                    Sender = "系统",
                    Content = $"发送失败：{ex.Message}",
                    IsStreaming = false
                });
            }
        }
        else
        {
            // 未连接时本地回复
            Messages.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = "Aether",
                Content = "设备未连接，请在设置中配置设备连接后重试。",
                IsStreaming = false
            });
        }
    }

    /// <summary>异步用 LLM 生成标题并更新当前会话（持久化 + 侧栏项）。</summary>
    private async Task GenerateAndApplyTitleAsync(string userMessage, string assistantReply)
    {
        if (_currentConversationId == Guid.Empty) return;
        try
        {
            var title = await _titleService.GenerateTitleAsync(userMessage, assistantReply);
            if (string.IsNullOrWhiteSpace(title)) return;

            var conv = await _conversationStore.GetAsync(_currentConversationId);
            if (conv is null) return;
            conv.Title = title;
            await _conversationStore.SaveAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null) item.Title = title;
            });
        }
        catch { /* 标题生成失败不影响主流程 */ }
    }

    /// <summary>把本轮 user+assistant 消息追加到当前会话并持久化 + 乐观 bump 侧栏项。</summary>
    private async Task PersistTurnAsync(ExpertItem expert, string userContent, string assistantContent)
    {
        if (_currentConversationId == Guid.Empty) return;
        try
        {
            var conv = await _conversationStore.GetAsync(_currentConversationId);
            if (conv is null) return;
            // user 已在创建时追加过；非首轮回补
            var last = conv.Messages.Count > 0 ? conv.Messages[^1] : null;
            if (last is null || last.Role != Aether.AI.Core.Messages.MessageRole.User || last.Content != userContent)
                conv.Append(Aether.AI.Core.Messages.Message.User(userContent));
            if (!string.IsNullOrWhiteSpace(assistantContent))
                conv.Append(Aether.AI.Core.Messages.Message.Assistant(assistantContent));
            await _conversationStore.SaveAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null)
                {
                    item.Preview = assistantContent.Length > 40 ? assistantContent[..40] + "…" : assistantContent;
                    item.DisplayTime = FormatTime(conv.UpdatedAt);
                    // 移到顶部
                    Conversations.Remove(item);
                    Conversations.Insert(0, item);
                }
            });
        }
        catch { /* transcript 持久化失败不阻塞对话 */ }
    }
}
