using System;
using System.Collections.ObjectModel;
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

public partial class NewFriendsViewModel : NavigableViewModelBase
{
    private readonly IRelationshipService? _relationshipService;
    private readonly IEventBusService? _eventBusService;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public ObservableCollection<PendingRequestItem> PendingRequests { get; } = new();

    public NewFriendsViewModel(IRelationshipService? relationshipService = null,
                               IEventBusService? eventBusService = null,
                               IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _relationshipService = relationshipService;
        _eventBusService = eventBusService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        await LoadPendingRequestsAsync();
    }

    [RelayCommand]
    private async Task LoadPendingRequestsAsync()
    {
        IsLoading = true;
        StatusMessage = string.Empty;
        PendingRequests.Clear();

        try
        {
            if (_relationshipService == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }

            // 获取待处理请求（服务端已筛选，返回裸 List<Relationship>）
            var contacts = await Task.Run(() => _relationshipService.GetPendingRequestsAsync());
            if (contacts != null)
            {
                foreach (var r in contacts)
                {
                    var type = (RelationshipType)r.Type;
                    string displayType;
                    switch (type)
                    {
                        case RelationshipType.Friendship:
                            displayType = "好友";
                            break;
                        case RelationshipType.Device:
                            displayType = "设备";
                            break;
                        case RelationshipType.Group:
                            displayType = "群组";
                            break;
                        default:
                            // 待处理请求仅支持好友/设备/群组
                            continue;
                    }

                    PendingRequests.Add(new PendingRequestItem
                    {
                        Id = r.Id.ToString(),
                        Name = r.TargetName,
                        Initial = string.IsNullOrEmpty(r.TargetName) ? "?" : r.TargetName[..1].ToUpper(),
                        Type = displayType,
                        RelationshipType = type,
                        Message = "",
                        CreatedAt = FormatTime(r.CreationTime),
                        Status = (RelationshipStatus)r.Status
                    });
                }

                if (PendingRequests.Count == 0)
                {
                    StatusMessage = "暂无待处理请求";
                }
            }
            else
            {
                StatusMessage = "加载请求失败";
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
    private async Task AcceptRequestAsync(PendingRequestItem item)
    {
        if (item == null) return;

        StatusMessage = $"正在接受 {item.Name} 的{item.Type}请求...";
        System.Diagnostics.Debug.WriteLine($"[AcceptRequest] 开始接受请求 - Name: {item.Name}, Type: {item.Type}, RelationshipType: {item.RelationshipType}, Id: {item.Id}");

        try
        {
            if (_relationshipService == null)
            {
                StatusMessage = "服务未初始化";
                System.Diagnostics.Debug.WriteLine("[AcceptRequest] 错误: _relationshipService 为 null");
                return;
            }

            var relationshipType = (int)item.RelationshipType;
            System.Diagnostics.Debug.WriteLine($"[AcceptRequest] 调用 API - relationshipType: {relationshipType}, relationshipId: {item.Id}");

            // AcceptRelationshipAsync 返回裸 Relationship（失败抛异常，由 catch 处理）
            await Task.Run(() => _relationshipService.AcceptRelationshipAsync(relationshipType, item.Id));

            System.Diagnostics.Debug.WriteLine("[AcceptRequest] API 返回成功");

            StatusMessage = $"已成功接受 {item.Name} 的{item.Type}请求";
            System.Diagnostics.Debug.WriteLine($"[AcceptRequest] 成功接受请求，移除待处理项");
            PendingRequests.Remove(item);
            _eventBusService?.Publish(new ContactChangedEvent("accepted"));
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[AcceptRequest] 异常 - Message: {ex.Message}, StackTrace: {ex.StackTrace}");
        }
    }

    [RelayCommand]
    private async Task RejectRequestAsync(PendingRequestItem item)
    {
        if (item == null) return;

        StatusMessage = $"正在拒绝 {item.Name} 的{item.Type}请求...";
        System.Diagnostics.Debug.WriteLine($"[RejectRequest] 开始拒绝请求 - Name: {item.Name}, Type: {item.Type}, RelationshipType: {item.RelationshipType}, Id: {item.Id}");

        try
        {
            if (_relationshipService == null)
            {
                StatusMessage = "服务未初始化";
                System.Diagnostics.Debug.WriteLine("[RejectRequest] 错误: _relationshipService 为 null");
                return;
            }

            var relationshipType = (int)item.RelationshipType;
            System.Diagnostics.Debug.WriteLine($"[RejectRequest] 调用 API - relationshipType: {relationshipType}, relationshipId: {item.Id}");

            // RejectRelationshipAsync 返回裸 Relationship（失败抛异常，由 catch 处理）
            await Task.Run(() => _relationshipService.RejectRelationshipAsync(relationshipType, item.Id));

            System.Diagnostics.Debug.WriteLine("[RejectRequest] API 返回成功");

            StatusMessage = $"已拒绝 {item.Name} 的{item.Type}请求";
            System.Diagnostics.Debug.WriteLine($"[RejectRequest] 成功拒绝请求，移除待处理项");
            PendingRequests.Remove(item);
        }
        catch (Exception ex)
        {
            StatusMessage = $"操作失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[RejectRequest] 异常 - Message: {ex.Message}, StackTrace: {ex.StackTrace}");
        }
    }

    [RelayCommand]
    private Task GoBackAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        return Task.CompletedTask;
    }

    private static DateTime EnsureLocalTime(DateTime dt)
    {
        return dt.Kind == DateTimeKind.Local
            ? dt
            : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
    }

    private string FormatTime(DateTime creationTime)
    {
        var localTime = EnsureLocalTime(creationTime);
        var now = DateTime.Now;
        var diff = now - localTime;

        if (diff.TotalSeconds < 60)
        {
            return "刚刚";
        }
        else if (diff.TotalMinutes < 60)
        {
            return $"{(int)diff.TotalMinutes}分钟前";
        }
        else if (diff.TotalHours < 24)
        {
            return $"{(int)diff.TotalHours}小时前";
        }
        else if (diff.TotalDays < 7)
        {
            return $"{(int)diff.TotalDays}天前";
        }
        else
        {
            return localTime.ToString("yyyy-MM-dd");
        }
    }
}

public class PendingRequestItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Initial { get; set; } = "?";
    public string Type { get; set; } = string.Empty;
    public RelationshipType RelationshipType { get; set; }
    public string Message { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public RelationshipStatus Status { get; set; }
}