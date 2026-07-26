using System.Collections.ObjectModel;
using System.Windows.Input;
using Acme.Agents.AI;
using Acme.Agents.Shell;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

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
        Tenant = tenant;

        Notes = new NotesViewModel(serviceProvider);
        Experts = new ExpertsViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            _agentRunner,
            _dispatcher);
        Experts.SummonChanged += RefreshAgentSelectItems;

        SubscribeToConnectionStateChanges();
    }

    public string Tenant { get; }

    public ISettingsDialogService SettingsDialog => _settingsDialog;

    private void SubscribeToConnectionStateChanges()
    {
        _deviceChatClient.ConnectionStateChanged += status =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = _deviceChatClient.IsConnected;
                StatusText = IsConnected ? string.Empty : status;
            });
        };

        _deviceChatClient.OnMessageReceived += HandleMessageReceived;
    }

    public void RefreshConnectionState()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = _deviceChatClient.IsConnected;
            StatusText = IsConnected ? "已连接" : "未连接";
        });
    }

    private Guid _recentConversationId;

    /// <summary>收到远程 SignalR 消息回调（SignalR 线程）</summary>
    private async Task HandleMessageReceived(DeviceMessage msg)
    {
        _recentConversationId = msg.ConversationId;

        // 远程消息显示在右侧（与本机发送一致，参考 AlphaAgent）
        await Dispatcher.UIThread.InvokeAsync(() =>
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
            await Dispatcher.UIThread.InvokeAsync(() => Messages.Add(assistantMsg));
            try
            {
                await _agentRunner.RunStreamingAsync(expert.Id, msg.Content,
                    async (token, _) =>
                    {
                        await _dispatcher.InvokeAsync(() => assistantMsg.Content += token);
                    });
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                    assistantMsg.Content += $"\n\n[运行失败：{ex.Message}]");
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => assistantMsg.IsStreaming = false);
            }
            return;
        }

        // 未选用专家时本地简单回复
        await Dispatcher.UIThread.InvokeAsync(() =>
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

    /// <summary>是否是占位页（非聊天/笔记/专家）</summary>
    public bool IsPlaceholderPage => !IsChatPage && !IsNotesPage && !IsExpertsPage;

    /// <summary>笔记页 ViewModel</summary>
    public NotesViewModel Notes { get; }

    /// <summary>专家页 ViewModel</summary>
    public ExpertsViewModel Experts { get; }

    /// <summary>导航项集合（租户填充）。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    partial void OnCurrentPageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsNotesPage));
        OnPropertyChanged(nameof(IsExpertsPage));
        OnPropertyChanged(nameof(IsPlaceholderPage));
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
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

        await LoadLlmConfigsAsync();
        await Notes.LoadAsync();
        await Experts.LoadAsync();
        RefreshAgentSelectItems();
        await TryAutoConnectDeviceAsync();
    }

    /// <summary>加载 LLM 配置列表到下拉框</summary>
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
                // 默认选中 IsDefault 的，否则选第一个
                SelectedLlmItem = configs.FirstOrDefault(c => c.IsDefault) ?? configs.FirstOrDefault();
            }
        }
        catch
        {
        }
    }

    /// <summary>刷新聊天页 Agent 下拉框（已召唤的专家）。</summary>
    private void RefreshAgentSelectItems()
    {
        Dispatcher.UIThread.Post(() =>
        {
            AgentSelectItems.Clear();
            foreach (var e in Experts.GetSummonedExperts())
                AgentSelectItems.Add(e);
        });
    }

    private async Task TryAutoConnectDeviceAsync()
    {
        try
        {
            var vm = _serviceProvider.GetRequiredService<SettingsViewModel>();
            await vm.LoadAsync();
            await vm.AutoConnectIfEnabledAsync();
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
            var assistantMsg = new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = expert.Name,
                Content = string.Empty,
                IsStreaming = true,
            };
            Messages.Add(assistantMsg);
            try
            {
                await _agentRunner.RunStreamingAsync(expert.Id, content,
                    async (token, _) =>
                    {
                        await _dispatcher.InvokeAsync(() => assistantMsg.Content += token);
                    });
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                    assistantMsg.Content += $"\n\n[运行失败：{ex.Message}]");
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => assistantMsg.IsStreaming = false);
            }
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
}
