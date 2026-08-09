using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 创建群聊 ViewModel：输入群名称、描述、邀请用户名列表，调用 IGroupService.CreateGroupAsync。
/// </summary>
public partial class CreateGroupViewModel : NavigableViewModelBase
{
    private readonly IGroupService? _groupService;
    private readonly IEventBusService? _eventBusService;

    [ObservableProperty]
    private string _groupName = string.Empty;

    [ObservableProperty]
    private string _groupDescription = string.Empty;

    [ObservableProperty]
    private string _inviteUsernamesText = string.Empty;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public CreateGroupViewModel(IGroupService? groupService = null,
                                IEventBusService? eventBusService = null,
                                IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _groupService = groupService;
        _eventBusService = eventBusService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        if (_groupService == null)
        {
            StatusMessage = "服务未初始化";
            return;
        }

        if (string.IsNullOrWhiteSpace(GroupName))
        {
            StatusMessage = "请输入群名称";
            return;
        }

        IsCreating = true;
        StatusMessage = string.Empty;

        try
        {
            // 解析邀请用户名（逗号分隔，去空格）
            List<string>? inviteUsernames = null;
            if (!string.IsNullOrWhiteSpace(InviteUsernamesText))
            {
                inviteUsernames = InviteUsernamesText
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            // CreateGroupAsync 返回裸 Group（失败抛异常，由 catch 处理）
            await Task.Run(async () =>
                await _groupService.CreateGroupAsync(GroupName.Trim(), GroupDescription.Trim(), inviteUsernames));

            StatusMessage = "群聊创建成功！";

            // 通知通讯录刷新
            _eventBusService?.Publish(new ContactChangedEvent("group_created"));

            // 导航返回
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建失败: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }
}