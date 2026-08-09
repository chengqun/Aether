using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ContactDetailViewModel : NavigableViewModelBase
{
    private readonly IRelationshipService? _relationshipService;
    private readonly IEventBusService? _eventBusService;

    [ObservableProperty]
    private string _contactId = string.Empty;

    [ObservableProperty]
    private string _relationshipId = string.Empty;

    [ObservableProperty]
    private string _contactName = "联系人详情";

    [ObservableProperty]
    private string _contactInitial = "?";

    [ObservableProperty]
    private string _contactType = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    /// <summary>
    /// 返回上一页并刷新通讯录事件
    /// </summary>
    public event EventHandler? PopAndRefreshRequested;

    public ContactDetailViewModel(IRelationshipService? relationshipService = null,
                                  IEventBusService? eventBusService = null,
                                  IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _relationshipService = relationshipService;
        _eventBusService = eventBusService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    /// <summary>
    /// 设置导航参数（由平台层调用）
    /// </summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id))
            ContactId = id?.ToString() ?? string.Empty;

        if (query.TryGetValue("relationshipId", out var relationshipId))
            RelationshipId = relationshipId?.ToString() ?? string.Empty;

        if (query.TryGetValue("name", out var name))
            ContactName = name?.ToString() ?? "联系人详情";

        if (query.TryGetValue("initial", out var initial))
            ContactInitial = initial?.ToString() ?? "?";

        if (query.TryGetValue("type", out var type))
            ContactType = type?.ToString() ?? string.Empty;
    }

    [RelayCommand]
    private Task ViewMomentsAsync()
    {
        if (string.IsNullOrEmpty(ContactId))
        {
            StatusMessage = "无法查看该用户的朋友圈";
            return Task.CompletedTask;
        }

        string type = ContactType switch
        {
            "好友" => "friendship",
            "股票" => "stock",
            "设备" => "device",
            "群组" => "group",
            "服务号" => "serviceaccount",
            _ => "friendship"
        };

        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
            "ContactMomentsPage",
            new Dictionary<string, object>
            {
                ["targetId"] = ContactId,
                ["targetType"] = type,
                ["username"] = ContactName
            }));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SendMessageAsync()
    {
        if (string.IsNullOrEmpty(ContactId))
        {
            StatusMessage = "无法发送消息";
            return Task.CompletedTask;
        }

        try
        {
            // 服务号不支持发消息
            if (ContactType == "服务号")
            {
                StatusMessage = "服务号不支持发消息";
                return Task.CompletedTask;
            }

            // 股票类型走 Agent 通道
            if (ContactType == "股票")
            {
                NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
                    "AgentChatDetailPage",
                    new Dictionary<string, object>
                    {
                        ["stockId"] = ContactId,
                        ["stockName"] = ContactName
                    }));
                return Task.CompletedTask;
            }

            // 其他类型走普通聊天
            var conversationType = ContactType == "群组" ? 1 : 0;
            NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(
                "ChatDetailPage",
                new Dictionary<string, object>
                {
                    ["pendingContactId"] = ContactId,
                    ["pendingContactType"] = ContactType,
                    ["conversationName"] = ContactName,
                    ["conversationType"] = conversationType
                }));
        }
        catch (Exception ex)
        {
            StatusMessage = $"导航失败: {ex.Message}";
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task DeleteRelationshipAsync()
    {
        if (string.IsNullOrEmpty(RelationshipId))
        {
            StatusMessage = "无法删除关系";
            System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 错误: RelationshipId 为空");
            return;
        }

        if (_relationshipService == null)
        {
            StatusMessage = "服务未初始化";
            System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 错误: _relationshipService 为 null");
            return;
        }

        var relationshipType = ContactType switch
        {
            "好友" => (int)RelationshipType.Friendship,
            "股票" => (int)RelationshipType.Stock,
            "设备" => (int)RelationshipType.Device,
            "群组" => (int)RelationshipType.Group,
            "服务号" => (int)RelationshipType.ServiceAccount,
            _ => (int)RelationshipType.Friendship
        };

        StatusMessage = $"正在删除与 {ContactName} 的关系...";
        System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 开始删除 - RelationshipId: {RelationshipId}, ContactId: {ContactId}, ContactName: {ContactName}, ContactType: {ContactType}, RelationshipType: {relationshipType}");

        try
        {
            System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 调用 API - relationshipType: {relationshipType}, relationshipId: {RelationshipId}");

            // RemoveRelationshipAsync 返回 void Task（失败抛异常，由 catch 处理）
            await Task.Run(() => _relationshipService.RemoveRelationshipAsync(relationshipType, RelationshipId));

            System.Diagnostics.Debug.WriteLine("[DeleteRelationship] API 返回成功");

            System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 删除成功，准备返回上一页");
            _eventBusService?.Publish(new ContactChangedEvent("deleted"));
            PopAndRefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[DeleteRelationship] 异常 - Message: {ex.Message}, StackTrace: {ex.StackTrace}");
        }
    }
}