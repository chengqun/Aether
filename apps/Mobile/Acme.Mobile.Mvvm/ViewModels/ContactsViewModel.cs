using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ContactsViewModel : NavigableViewModelBase
{
    private readonly IRelationshipService? _relationshipService;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IEventBusService? _eventBusService;
    private readonly IContactSyncService? _contactSyncService;
    private bool _isLoaded = false;
    private bool _isSubscribed = false;
    private bool _forceRefresh = false;
    private DateTime _lastSyncTime = DateTime.MinValue;
    private static readonly TimeSpan _minSyncInterval = TimeSpan.FromSeconds(30);

    // 记录上次渲染的联系人 ID 集合，用于增量更新时判断数据是否变化
    private HashSet<string> _lastRenderedIds = new();

    [ObservableProperty]
    private string _title = "通讯录";

    [ObservableProperty]
    private string _statusMessage = "加载中...";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ContactItem? _selectedContact;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// 详情区 ViewModel（供桌面端两栏布局使用）
    /// </summary>
    [ObservableProperty]
    private ViewModelBase? _detailViewModel;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public ObservableCollection<ContactGroup> ContactGroups { get; } = new ObservableCollection<ContactGroup>();

    public ContactsViewModel(IRelationshipService? relationshipService = null,
                             ICustomAgentCatalog? agentCatalog = null,
                             IEventBusService? eventBusService = null,
                             IContactSyncService? contactSyncService = null,
                             IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _relationshipService = relationshipService;
        _agentCatalog = agentCatalog;
        _eventBusService = eventBusService;
        _contactSyncService = contactSyncService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        SubscribeEvents();

        if (!_isLoaded)
        {
            await LoadFromCacheAsync();
            _ = SyncInBackgroundAsync();
            _isLoaded = true;
        }
        else if (_forceRefresh || ShouldBackgroundSync())
        {
            _forceRefresh = false;
            _ = SyncInBackgroundAsync();
        }
    }

    private async Task LoadFromCacheAsync()
    {
        try
        {
            var (userId, cachedData) = await Task.Run(async () =>
            {
                var uid = await GetCurrentUserIdAsync();
                var data = _contactSyncService != null
                    ? await _contactSyncService.GetCachedContactsAsync(uid)
                    : null;
                return (uid, data);
            });

            if (cachedData != null && cachedData.Count > 0)
            {
                RenderContactBook(ContactBookBuilder.Build(cachedData));
                return;
            }

            // 无缓存时走服务端（首次使用）
            IsLoading = true;
            await LoadContactsFromApiAsync();
            IsLoading = false;
            _isLoaded = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContactsViewModel] 加载缓存失败: {ex.Message}");
        }
    }

    private async Task SyncInBackgroundAsync()
    {
        if (_contactSyncService == null) return;

        try
        {
            var serverData = await Task.Run(async () =>
            {
                var userId = await GetCurrentUserIdAsync();
                return await _contactSyncService.SyncFromServerAsync(userId);
            });

            if (serverData != null)
            {
                var book = ContactBookBuilder.Build(serverData);
                _uiThreadDispatcher?.Post(() =>
                {
                    // 增量更新：只在数据变化时才刷新 UI
                    UpdateContactBookIfNeeded(book);
                    _lastSyncTime = DateTime.UtcNow;
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContactsViewModel] 后台同步失败: {ex.Message}");
        }
    }

    private bool ShouldBackgroundSync()
    {
        return DateTime.UtcNow - _lastSyncTime > _minSyncInterval;
    }

    private async Task<Guid> GetCurrentUserIdAsync()
    {
        return new Guid("11111111-1111-1111-1111-111111111111");
    }

    /// <summary>
    /// 判断数据是否变化，只在变化时才重建 UI（避免闪烁）
    /// </summary>
    private void UpdateContactBookIfNeeded(ContactBookDto contacts)
    {
        var newIds = new HashSet<string>();
        foreach (var f in contacts.Friends) newIds.Add($"friend_{f.TargetId}");
        foreach (var g in contacts.Groups) newIds.Add($"group_{g.TargetId}");
        foreach (var d in contacts.Devices) newIds.Add($"device_{d.TargetId}");
        foreach (var s in contacts.Stocks) newIds.Add($"stock_{s.TargetId}");
        foreach (var sa in contacts.ServiceAccounts) newIds.Add($"serviceaccount_{sa.TargetId}");

        if (newIds.SetEquals(_lastRenderedIds))
            return; // 数据没变，不刷新

        RenderContactBook(contacts);
    }

    /// <summary>
    /// 全量重建通讯录 UI（仅在数据变化或首次加载时调用）
    /// </summary>
    private void RenderContactBook(ContactBookDto contacts)
    {
        // 收集新数据的 ID 集合
        var newIds = new HashSet<string>();
        foreach (var f in contacts.Friends) newIds.Add($"friend_{f.TargetId}");
        foreach (var g in contacts.Groups) newIds.Add($"group_{g.TargetId}");
        foreach (var d in contacts.Devices) newIds.Add($"device_{d.TargetId}");
        foreach (var s in contacts.Stocks) newIds.Add($"stock_{s.TargetId}");
        foreach (var sa in contacts.ServiceAccounts) newIds.Add($"serviceaccount_{sa.TargetId}");
        _lastRenderedIds = newIds;

        ContactGroups.Clear();

        // 添加"新的朋友"特殊分组
        var newFriendItem = new ContactItem
        {
            Id = "newfriends",
            Name = "新的朋友",
            Type = "",
            ActionType = "NewFriends"
        };
        newFriendItem.SetCustomIconSource("addfriend");
        ContactGroups.Add(new ContactGroup
        {
            Title = "",
            Contacts = new ObservableCollection<ContactItem> { newFriendItem }
        });

        var acceptedFriends = contacts.Friends.ToList();
        if (acceptedFriends.Count > 0)
        {
            ContactGroups.Add(new ContactGroup
            {
                Title = $"好友 ({acceptedFriends.Count})",
                Contacts = new ObservableCollection<ContactItem>(
                    acceptedFriends.Select(f => new ContactItem
                    {
                        Id = f.TargetId,
                        RelationshipId = f.Id.ToString(),
                        Name = f.TargetName,
                        Initial = f.Initial,
                        Type = "好友"
                    }))
            });
        }

        var acceptedGroups = contacts.Groups.ToList();
        if (acceptedGroups.Count > 0)
        {
            ContactGroups.Add(new ContactGroup
            {
                Title = $"群组 ({acceptedGroups.Count})",
                Contacts = new ObservableCollection<ContactItem>(
                    acceptedGroups.Select(g => new ContactItem
                    {
                        Id = g.TargetId,
                        RelationshipId = g.Id.ToString(),
                        Name = g.TargetName,
                        Initial = g.Initial,
                        Type = "群组"
                    }))
            });
        }

        var acceptedDevices = contacts.Devices.ToList();
        if (acceptedDevices.Count > 0)
        {
            ContactGroups.Add(new ContactGroup
            {
                Title = $"设备 ({acceptedDevices.Count})",
                Contacts = new ObservableCollection<ContactItem>(
                    acceptedDevices.Select(d => new ContactItem
                    {
                        Id = d.TargetId,
                        RelationshipId = d.Id.ToString(),
                        Name = d.TargetName,
                        Initial = d.Initial,
                        Type = "设备",
                        DeviceType = d.DeviceType
                    }))
            });
        }

        var acceptedStocks = contacts.Stocks.ToList();
        if (acceptedStocks.Count > 0)
        {
            ContactGroups.Add(new ContactGroup
            {
                Title = $"股票 ({acceptedStocks.Count})",
                Contacts = new ObservableCollection<ContactItem>(
                    acceptedStocks.Select(s => new ContactItem
                    {
                        Id = s.TargetId,
                        RelationshipId = s.Id.ToString(),
                        Name = s.TargetName,
                        Initial = s.Initial,
                        Type = "股票"
                    }))
            });
        }

        var acceptedServiceAccounts = contacts.ServiceAccounts.ToList();
        if (acceptedServiceAccounts.Count > 0)
        {
            ContactGroups.Add(new ContactGroup
            {
                Title = $"服务号 ({acceptedServiceAccounts.Count})",
                Contacts = new ObservableCollection<ContactItem>(
                    acceptedServiceAccounts.Select(sa => new ContactItem
                    {
                        Id = sa.TargetId,
                        RelationshipId = sa.Id.ToString(),
                        Name = sa.TargetName,
                        Initial = sa.Initial,
                        Type = "服务号"
                    }))
            });
        }

        _ = LoadAiAgentsAsync();

        if (ContactGroups.Sum(g => g.Contacts.Count) == 1)
        {
            StatusMessage = "暂无联系人";
        }
        else
        {
            StatusMessage = string.Empty;
        }
    }

    private void SubscribeEvents()
    {
        if (_isSubscribed) return;
        _isSubscribed = true;
        _eventBusService?.Subscribe<ContactChangedEvent>(OnContactChanged);
        _eventBusService?.Subscribe<AgentsChangedEvent>(OnAgentsChanged);
    }

    private void OnAgentsChanged(AgentsChangedEvent @event)
    {
        // 仅刷新 AI 助手分组（召唤/删除后），不走全量通讯录服务端同步
        _uiThreadDispatcher?.Post(async () => await LoadAiAgentsAsync());
    }

    private async void OnContactChanged(ContactChangedEvent @event)
    {
        _forceRefresh = true;
        _lastSyncTime = DateTime.MinValue;
        await SyncInBackgroundAsync();
    }

    private async Task LoadContactsFromApiAsync()
    {
        if (_relationshipService == null) return;

        var contacts = await Task.Run(() => _relationshipService.GetAcceptedContactsAsync());
        // null=HTTP 失败 → 报"加载失败"；非null（哪怕空）= 成功 → 渲染通讯录（空数据也要渲染，
        // 让"新的朋友"入口常驻，否则用户无法加好友 → 死循环没数据）。对齐 AlphaAgent 的 response.Success 语义。
        if (contacts != null)
        {
            RenderContactBook(ContactBookBuilder.Build(contacts));
        }
        else
        {
            await LoadAiAgentsAsync();
            StatusMessage = "通讯录加载失败";
        }
    }

    private async Task LoadAiAgentsAsync()
    {
        if (_agentCatalog == null) return;

        try
        {
            var agents = await _agentCatalog.ListAsync();

            // 幂等替换：先移除旧的 AI 助手分组，再重新添加（召唤/删除后可重复调用而不重复分组）
            for (int i = ContactGroups.Count - 1; i >= 0; i--)
            {
                if (ContactGroups[i].Contacts.Any(c => c.ActionType == "AgentDetail"))
                    ContactGroups.RemoveAt(i);
            }

            if (agents.Any())
            {
                ContactGroups.Add(new ContactGroup
                {
                    Title = $"AI 助手 ({agents.Count})",
                    Contacts = new ObservableCollection<ContactItem>(
                        agents.Select(a => new ContactItem
                        {
                            Id = a.Name,
                            Name = a.Name,
                            Initial = a.Name[0].ToString(),
                            Type = "AI助手",
                            ActionType = "AgentDetail",
                            AgentKind = a.NodeType switch
                            {
                                NoteNodeType.Workflow => AgentKind.Workflow,
                                NoteNodeType.Rule or NoteNodeType.CodeRule => AgentKind.RuleBased,
                                _ => AgentKind.Agent,
                            }
                        }))
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContactsViewModel] 加载 AI 助手失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RefreshContactsAsync()
    {
        IsLoading = true;
        try
        {
            if (_contactSyncService != null)
            {
                var data = await Task.Run(async () =>
                {
                    var userId = await GetCurrentUserIdAsync();
                    return await _contactSyncService.SyncFromServerAsync(userId);
                });
                if (data != null)
                    RenderContactBook(ContactBookBuilder.Build(data));
            }
            else
            {
                await LoadContactsFromApiAsync();
            }
            _lastSyncTime = DateTime.UtcNow;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void SelectContact(ContactItem contact)
    {
        SelectedContact = contact;
    }

    [RelayCommand]
    private Task NavigateToContactDetailAsync(ContactItem contact)
    {
        if (contact == null) return Task.CompletedTask;

        if (!string.IsNullOrEmpty(contact.ActionType))
        {
            switch (contact.ActionType)
            {
                case "NewFriends":
                    NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("NewFriendsPage"));
                    return Task.CompletedTask;
                case "AgentDetail":
                    NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
                        "AgentContactDetailPage",
                        new Dictionary<string, object> { ["agentName"] = contact.Name }));
                    return Task.CompletedTask;
            }
        }

        // 服务号走详情页
        if (contact.Type == "服务号")
        {
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
                "ServiceAccountDetailPage",
                new Dictionary<string, object> { ["id"] = contact.Id }));
            return Task.CompletedTask;
        }

        // 群组走群详情页（含成员管理）
        if (contact.Type == "群组")
        {
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
                "GroupDetailPage",
                new Dictionary<string, object>
                {
                    ["groupId"] = contact.Id,
                    ["groupName"] = contact.Name
                }));
            return Task.CompletedTask;
        }

        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
            "ContactDetailPage",
            new Dictionary<string, object>
            {
                ["id"] = contact.Id,
                ["relationshipId"] = contact.RelationshipId,
                ["name"] = contact.Name,
                ["initial"] = contact.Initial,
                ["type"] = contact.Type
            }));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task AddFriendAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("AddFriendPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NewFriendsAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("NewFriendsPage"));
        return Task.CompletedTask;
    }
}

public partial class ContactGroup : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public ObservableCollection<ContactItem> Contacts { get; set; } = new ObservableCollection<ContactItem>();

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// 折叠/展开指示器文本
    /// </summary>
    public string ExpandIndicator => IsExpanded ? "▼" : "▶";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandIndicator));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

public class ContactItem : ObservableObject
{
    private string? _deviceType;
    private string? _customIconSource;

    public string Id { get; set; } = string.Empty;
    public string RelationshipId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Initial { get; set; } = "?";
    public string Type { get; set; } = string.Empty;
    public string? ActionType { get; set; }

    /// <summary>
    /// Agent 类型分类（仅 AI 助手有值）
    /// </summary>
    public AgentKind? AgentKind { get; set; }

    /// <summary>
    /// 是否为 LLM Agent 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindAgent => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.Agent;

    /// <summary>
    /// 是否为 Workflow 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindWorkflow => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.Workflow;

    /// <summary>
    /// 是否为 RuleBased 类型（用于 XAML 绑定）
    /// </summary>
    public bool IsAgentKindRuleBased => AgentKind == Acme.Mobile.Mvvm.ViewModels.AgentKind.RuleBased;

    public string? DeviceType
    {
        get => _deviceType;
        set
        {
            if (SetProperty(ref _deviceType, value))
            {
                OnPropertyChanged(nameof(IconSource));
            }
        }
    }

    public void SetCustomIconSource(string? iconSource)
    {
        _customIconSource = iconSource;
        OnPropertyChanged(nameof(IconSource));
    }

    public string? IconSource
    {
        get
        {
            if (!string.IsNullOrEmpty(_customIconSource))
                return _customIconSource;

            if (!string.IsNullOrEmpty(DeviceType))
            {
                if (DeviceType.Contains("windows", StringComparison.OrdinalIgnoreCase))
                    return "windows";
                if (DeviceType.Contains("macos", StringComparison.OrdinalIgnoreCase))
                    return "macos";
                if (DeviceType.Contains("claude-bridge", StringComparison.OrdinalIgnoreCase))
                    return "claude_bridge";
            }
            return null;
        }
    }
}