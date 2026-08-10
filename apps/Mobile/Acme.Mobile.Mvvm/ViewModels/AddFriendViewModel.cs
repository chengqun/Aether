using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ChatSocial.Domain;
using Acme.Securities.Application;
using Acme.AppUpdate.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.Services;
using Microsoft.Extensions.Logging;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class AddFriendViewModel : NavigableViewModelBase
{
    private readonly IRelationshipService? _relationshipService;
    private readonly IEventBusService? _eventBusService;
    private readonly ISecurityService? _securityService;
    private readonly ILogger<AddFriendViewModel>? _logger;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    public AddFriendViewModel(IRelationshipService? relationshipService = null,
                              IEventBusService? eventBusService = null,
                              ISecurityService? securityService = null,
                              ILogger<AddFriendViewModel>? logger = null,
                              IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _relationshipService = relationshipService;
        _eventBusService = eventBusService;
        _securityService = securityService;
        _logger = logger;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            StatusMessage = "请输入搜索内容";
            return;
        }

        IsLoading = true;
        StatusMessage = string.Empty;
        SearchResults.Clear();

        try
        {
            if (_relationshipService == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }

            // 服务端统一搜索（用户/群/股票/设备/服务号）——镜像 AlphaAgent SearchAllTargetsAsync。
            // 之前移植时去掉了此调用、改 client 端分类型搜（只覆盖股票+服务号），导致搜用户/群无结果——本次恢复服务端搜索。
            var keyword = SearchText.Trim();
            var results = await Task.Run(() => _relationshipService.SearchAllTargetsAsync(keyword));

            // null=HTTP 失败（未登录/网络/服务器异常）；空列表=成功无匹配——二者必须区分。
            if (results == null)
            {
                StatusMessage = "搜索失败，请检查网络或重新登录后重试";
                return;
            }

            foreach (var target in results)
            {
                var relationshipType = target.Type switch
                {
                    "User" => RelationshipType.Friendship,
                    "Group" => RelationshipType.Group,
                    "Stock" => RelationshipType.Stock,
                    "Device" => RelationshipType.Device,
                    "ServiceAccount" => RelationshipType.ServiceAccount,
                    _ => RelationshipType.Friendship
                };

                var displayType = target.Type switch
                {
                    "User" => "用户",
                    "Group" => "群组",
                    "Stock" => "股票",
                    "Device" => "设备",
                    "ServiceAccount" => "服务号",
                    _ => target.Type
                };

                SearchResults.Add(new SearchResultItem
                {
                    Id = target.Id,
                    Name = target.Name,
                    Initial = GetInitial(target.Name),
                    Type = displayType,
                    RelationshipType = relationshipType,
                    SecurityInfo = target.SecurityInfo != null
                        ? new TargetSecurityInfo
                        {
                            Code = target.SecurityInfo.Code,
                            Name = target.Name,
                            SecurityType = target.SecurityInfo.SecurityType,
                            Exchange = target.SecurityInfo.Exchange,
                            BaseCode = target.SecurityInfo.BaseCode
                        }
                        : null
                });
            }

            if (SearchResults.Count == 0)
            {
                StatusMessage = "未找到匹配的结果";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddFriendAsync(SearchResultItem item)
    {
        if (item == null) return;

        StatusMessage = $"正在添加 {item.Name}...";
        
        try
        {
            if (_relationshipService == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }

            // CreateRelationshipAsync 返回裸 Relationship（失败抛异常，由 catch 处理）
            await Task.Run(() => _relationshipService.CreateRelationshipAsync((int)item.RelationshipType, item.Id));

            string successMessage = string.Empty;
            switch (item.RelationshipType)
            {
                case RelationshipType.Friendship:
                    successMessage = $"已向 {item.Name} 发送好友请求";
                    break;
                case RelationshipType.Group:
                    successMessage = $"已申请加入群组 {item.Name}";
                    break;
                case RelationshipType.Stock:
                    successMessage = $"已添加股票 {item.Name}";
                    _logger?.LogInformation("添加股票: Name={Name}, SecurityInfo={SecurityInfo}",
                        item.Name,
                        item.SecurityInfo != null
                            ? $"Code={item.SecurityInfo.Code}, Type={item.SecurityInfo.SecurityType}, Exchange={item.SecurityInfo.Exchange}"
                            : "NULL");
                    await SyncLocalSecurityAsync(item);
                    break;
                case RelationshipType.Device:
                    successMessage = $"已申请绑定设备 {item.Name}";
                    break;
                case RelationshipType.ServiceAccount:
                    successMessage = $"已关注服务号 {item.Name}";
                    break;
            }
            StatusMessage = successMessage;
            SearchResults.Remove(item);
            _eventBusService?.Publish(new ContactChangedEvent("added"));
        }
        catch (Exception ex)
        {
            StatusMessage = $"添加失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task GoBackAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
        return Task.CompletedTask;
    }

    private static string GetInitial(string name)
        => string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();

    private async Task SyncLocalSecurityAsync(SearchResultItem item)
    {
        _logger?.LogInformation("SyncLocalSecurityAsync 开始: item.SecurityInfo={SecurityInfo}", 
            item.SecurityInfo != null 
                ? $"Code={item.SecurityInfo.Code}, Type={item.SecurityInfo.SecurityType}" 
                : "NULL");

        if (_securityService == null)
        {
            _logger?.LogWarning("SyncLocalSecurityAsync 失败: _securityService 为 null");
            return;
        }

        if (item.SecurityInfo == null)
        {
            _logger?.LogWarning("SyncLocalSecurityAsync 失败: item.SecurityInfo 为 null");
            return;
        }

        try
        {
            // UpdateOrAddSecurityAsync 会检查证券是否已存在，存在则更新，不存在则新增
            _logger?.LogInformation("SyncLocalSecurityAsync 同步: Code={Code}, Name={Name}, Type={Type}, Exchange={Exchange}", 
                item.SecurityInfo.Code, item.Name, item.SecurityInfo.SecurityType, item.SecurityInfo.Exchange);
            
            var securityDto = new SecurityDto
            {
                Code = item.SecurityInfo.Code,
                Name = item.Name,
                Type = item.SecurityInfo.SecurityType,
                Exchange = item.SecurityInfo.Exchange,
                BaseCode = item.SecurityInfo.BaseCode
            };
            await Task.Run(() => _securityService.UpdateOrAddSecurityAsync(securityDto));
            
            _logger?.LogInformation("SyncLocalSecurityAsync 成功");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SyncLocalSecurityAsync 异常");
        }
    }
}

public class SearchResultItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Initial { get; set; } = "?";
    public string Type { get; set; } = string.Empty;
    public RelationshipType RelationshipType { get; set; }
    public TargetSecurityInfo? SecurityInfo { get; set; }
}