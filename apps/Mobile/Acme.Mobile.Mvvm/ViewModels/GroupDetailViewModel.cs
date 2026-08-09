using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 群详情 ViewModel：显示群信息、成员列表，Owner 可添加/删除成员、解散群。
/// </summary>
public partial class GroupDetailViewModel : NavigableViewModelBase
{
    private readonly IGroupService? _groupService;
    private readonly IRelationshipService? _relationshipService;
    private readonly IAppAuthService? _authService;
    private readonly IAppTokenStore? _tokenStore;
    private readonly IEventBusService? _eventBusService;

    [ObservableProperty]
    private string _groupId = string.Empty;

    [ObservableProperty]
    private string _groupName = string.Empty;

    [ObservableProperty]
    private string _groupDescription = string.Empty;

    [ObservableProperty]
    private int _memberCount;

    [ObservableProperty]
    private bool _isOwner;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _newMemberUsername = string.Empty;

    [ObservableProperty]
    private bool _isAddingMember;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isSelectFriendMode;

    /// <summary>群成员列表</summary>
    public ObservableCollection<GroupMember> Members { get; } = new();

    /// <summary>可选好友列表（从通讯录加载，排除已在群中的成员）</summary>
    public ObservableCollection<FriendSelectItem> AvailableFriends { get; } = new();

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>
    /// 返回并刷新通讯录事件
    /// </summary>
    public event EventHandler? PopAndRefreshRequested;

    public GroupDetailViewModel(IGroupService? groupService = null,
                                IRelationshipService? relationshipService = null,
                                IAppAuthService? authService = null,
                                IEventBusService? eventBusService = null,
                                IUIThreadDispatcher? uiThreadDispatcher = null,
                                IAppTokenStore? tokenStore = null)
    {
        _groupService = groupService;
        _relationshipService = relationshipService;
        _authService = authService;
        _tokenStore = tokenStore;
        _eventBusService = eventBusService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await LoadMembersAsync();
    }

    /// <summary>
    /// 设置群参数（由平台层调用）
    /// </summary>
    public void SetParameters(string groupId, string groupName, string? description = null)
    {
        GroupId = groupId;
        GroupName = groupName;
        GroupDescription = description ?? string.Empty;
    }

    [RelayCommand]
    public async Task LoadMembersAsync()
    {
        if (_groupService == null || string.IsNullOrEmpty(GroupId))
        {
            StatusMessage = "服务未初始化";
            return;
        }

        IsLoading = true;
        StatusMessage = string.Empty;

        try
        {
            // GetGroupMembersAsync 返回裸 List<GroupMember>（失败抛异常，由 catch 处理）
            var members = await Task.Run(async () => await _groupService.GetGroupMembersAsync(GroupId));

            if (members != null)
            {
                Members.Clear();
                foreach (var member in members)
                {
                    Members.Add(member);
                }
                MemberCount = Members.Count;

                // 判断当前用户是否是群主（后端 Role == "Owner"）
                if (_tokenStore != null)
                {
                    var currentUsername = await Task.Run(() => _tokenStore.GetUsernameAsync());
                    IsOwner = Members.Any(m => m.Role == "Owner" && m.Username == currentUsername);
                }
            }
            else
            {
                StatusMessage = "加载成员列表失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddMemberAsync()
    {
        if (_groupService == null || string.IsNullOrEmpty(GroupId))
            return;

        if (string.IsNullOrWhiteSpace(NewMemberUsername))
        {
            StatusMessage = "请输入用户名";
            return;
        }

        IsAddingMember = true;
        StatusMessage = string.Empty;

        try
        {
            // AddMemberAsync 返回 void Task（失败抛异常，由 catch 处理）
            await Task.Run(async () =>
                await _groupService.AddMemberAsync(GroupId, NewMemberUsername.Trim()));

            StatusMessage = "成员添加成功";
            NewMemberUsername = string.Empty;
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"添加失败: {ex.Message}";
        }
        finally
        {
            IsAddingMember = false;
        }
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(string? userId)
    {
        if (_groupService == null || string.IsNullOrEmpty(GroupId) || string.IsNullOrEmpty(userId))
            return;

        try
        {
            // RemoveMemberAsync 返回 void Task（失败抛异常，由 catch 处理）
            await Task.Run(async () =>
                await _groupService.RemoveMemberAsync(GroupId, userId));

            StatusMessage = "成员已移除";
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"移除失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LeaveGroupAsync()
    {
        if (_groupService == null || _tokenStore == null || string.IsNullOrEmpty(GroupId))
            return;

        try
        {
            var currentUsername = await Task.Run(() => _tokenStore.GetUsernameAsync());
            var currentUser = Members.FirstOrDefault(m => m.Username == currentUsername);
            if (currentUser == null)
            {
                StatusMessage = "无法退出群聊";
                return;
            }

            // RemoveMemberAsync 返回 void Task（失败抛异常，由 catch 处理）
            await Task.Run(async () =>
                await _groupService.RemoveMemberAsync(GroupId, currentUser.UserId));

            _eventBusService?.Publish(new ContactChangedEvent("group_left"));
            PopAndRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"退出失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisbandGroupAsync()
    {
        if (_groupService == null || string.IsNullOrEmpty(GroupId))
            return;

        try
        {
            // DisbandGroupAsync 返回 void Task（失败抛异常，由 catch 处理）
            await Task.Run(async () =>
                await _groupService.DisbandGroupAsync(GroupId));

            _eventBusService?.Publish(new ContactChangedEvent("group_disbanded"));
            PopAndRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"解散失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task SendMessageAsync()
    {
        if (string.IsNullOrEmpty(GroupId))
        {
            StatusMessage = "无法发送消息";
            return Task.CompletedTask;
        }

        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
            "ChatDetailPage",
            new System.Collections.Generic.Dictionary<string, object>
            {
                ["pendingContactId"] = GroupId,
                ["pendingContactType"] = "群组",
                ["conversationName"] = GroupName,
                ["conversationType"] = 1
            }));

        return Task.CompletedTask;
    }

    /// <summary>
    /// 切换到从通讯录选择好友模式
    /// </summary>
    [RelayCommand]
    private async Task ToggleSelectFriendModeAsync()
    {
        if (!IsSelectFriendMode)
        {
            // 进入选择模式，加载好友列表
            IsSelectFriendMode = true;
            await LoadAvailableFriendsAsync();
        }
        else
        {
            // 退出选择模式
            IsSelectFriendMode = false;
            AvailableFriends.Clear();
        }
    }

    private async Task LoadAvailableFriendsAsync()
    {
        if (_relationshipService == null)
        {
            StatusMessage = "服务未初始化";
            return;
        }

        try
        {
            // GetAcceptedContactsAsync 返回裸 List<Relationship>（失败抛异常，由 catch 处理）
            var contacts = await Task.Run(async () => await _relationshipService.GetAcceptedContactsAsync());

            if (contacts != null)
            {
                // 获取已在群中的用户名
                var memberUsernames = Members.Select(m => m.Username).ToHashSet();

                AvailableFriends.Clear();
                foreach (var r in contacts)
                {
                    // 仅好友关系可添加进群
                    if ((RelationshipType)r.Type != RelationshipType.Friendship)
                        continue;

                    // 排除已在群中的好友
                    if (!memberUsernames.Contains(r.TargetName))
                    {
                        AvailableFriends.Add(new FriendSelectItem
                        {
                            UserId = r.TargetId,
                            Username = r.TargetName,
                            Initial = string.IsNullOrEmpty(r.TargetName) ? "?" : r.TargetName[..1].ToUpper()
                        });
                    }
                }

                if (AvailableFriends.Count == 0)
                {
                    StatusMessage = "没有可添加的好友";
                }
            }
            else
            {
                StatusMessage = "加载好友列表失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载好友失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 添加选中的好友到群聊
    /// </summary>
    [RelayCommand]
    private async Task AddSelectedFriendsAsync()
    {
        if (_groupService == null || string.IsNullOrEmpty(GroupId))
            return;

        var selected = AvailableFriends.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "请选择要添加的好友";
            return;
        }

        IsAddingMember = true;
        StatusMessage = string.Empty;

        try
        {
            int successCount = 0;
            foreach (var friend in selected)
            {
                try
                {
                    // AddMemberAsync 返回 void Task（失败抛异常，跳过该成员）
                    await Task.Run(async () =>
                        await _groupService.AddMemberAsync(GroupId, friend.Username));
                    successCount++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AddSelectedFriends] 添加 {friend.Username} 失败: {ex.Message}");
                }
            }

            StatusMessage = $"成功添加 {successCount} 位成员";
            IsSelectFriendMode = false;
            AvailableFriends.Clear();
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"添加失败: {ex.Message}";
        }
        finally
        {
            IsAddingMember = false;
        }
    }
}

/// <summary>
/// 好友选择项（用于从通讯录选择添加到群聊）
/// </summary>
public partial class FriendSelectItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Initial { get; set; } = "?";

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected;
}