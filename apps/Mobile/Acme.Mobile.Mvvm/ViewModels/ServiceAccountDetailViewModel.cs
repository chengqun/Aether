using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.ServiceAccount.Domain;
using Acme.Mobile.Mvvm.Abstractions;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// 服务号详情页 ViewModel
/// 服务号基本信息从服务器获取（需要最新的关注状态），文章列表缓存优先加载
/// </summary>
public partial class ServiceAccountDetailViewModel : NavigableViewModelBase
{
    private readonly IServiceAccountService? _serviceAccountService;
    private readonly IServiceAccountCacheService? _cacheService;
    private readonly HashSet<string> _displayedPostIds = new();
    // _displayedPostIds 被 LoadCachedPostsAsync(主线程 dispatcher)/SyncIncrementalAsync(Task.Run 线程池)访问，
    // 此 VM 的 LoadAsync 同时启动两个后台任务，下拉刷新也能并发触发同步——HashSet 非线程安全，必须加锁。
    private readonly object _displayedPostIdsLock = new();
    private DateTime _lastSyncTime = DateTime.MinValue;
    private const int MinSyncIntervalSeconds = 30;

    [ObservableProperty]
    private Guid _serviceAccountId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _avatarUrl = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private bool _isVerified;

    [ObservableProperty]
    private bool _isFollowed;

    [ObservableProperty]
    private string _welcomeMessage = string.Empty;

    [ObservableProperty]
    private int _followerCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasWelcomeMessage;

    public ObservableCollection<ServiceAccountPostListItem> Posts { get; } = new();

    /// <summary>
    /// 导航到文章详情事件
    /// </summary>
    public event EventHandler<ServiceAccountPostNavigationEventArgs>? NavigateToPostDetailRequested;

    public ServiceAccountDetailViewModel(IServiceAccountService? serviceAccountService = null,
                                         IServiceAccountCacheService? cacheService = null,
                                         IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _serviceAccountService = serviceAccountService;
        _cacheService = cacheService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var id) && Guid.TryParse(id?.ToString(), out var guid))
        {
            ServiceAccountId = guid;
        }
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await LoadAsync();
    }

    /// <summary>
    /// 加载服务号详情和内容列表
    /// 缓存优先：先显示本地缓存的文章和服务号名称，再后台从服务器获取最新信息
    /// </summary>
    private async Task LoadAsync()
    {
        if (_serviceAccountService == null || ServiceAccountId == Guid.Empty)
        {
            return;
        }

        IsLoading = true;
        try
        {
            // 1. 先从缓存加载文章列表（秒开）
            await LoadCachedPostsAsync();

            // 2. 从缓存文章中提取服务号名称（首次打开即可显示）
            if (string.IsNullOrEmpty(Name) && Posts.Count > 0)
            {
                var firstPost = Posts[0];
                Name = firstPost.ServiceAccountName;
            }

            StatusMessage = Posts.Count == 0 ? "暂无内容" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
            Debug.WriteLine($"[ServiceAccountDetailViewModel] 缓存加载失败: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }

        // 3. 后台获取服务号完整信息（关注状态等）+ 增量同步文章
        _ = SyncInfoInBackgroundAsync();
        _ = SyncInBackgroundAsync();
    }

    /// <summary>
    /// 后台获取服务号完整信息（关注状态等需要从服务器获取的最新数据）
    /// </summary>
    private async Task SyncInfoInBackgroundAsync()
    {
        try
        {
            var info = await Task.Run(() => _serviceAccountService!.GetAsync(ServiceAccountId));
            if (info != null)
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    Name = info.Name;
                    AvatarUrl = info.AvatarUrl ?? string.Empty;
                    Description = info.Description ?? string.Empty;
                    Category = info.Category;
                    IsVerified = info.IsVerified;
                    IsFollowed = info.IsFollowedByCurrentUser;
                    WelcomeMessage = info.WelcomeMessage ?? string.Empty;
                    HasWelcomeMessage = !string.IsNullOrEmpty(info.WelcomeMessage);
                    FollowerCount = info.FollowerCount;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceAccountDetailViewModel] 后台获取服务号信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从本地 SQLite 缓存加载文章列表
    /// </summary>
    private async Task LoadCachedPostsAsync()
    {
        if (_cacheService == null) return;

        var cached = await Task.Run(() => _cacheService.GetCachedPostsAsync(ServiceAccountId));
        _uiThreadDispatcher?.Post(() =>
        {
            foreach (var post in cached)
            {
                bool added;
                lock (_displayedPostIdsLock) added = _displayedPostIds.Add(post.Id.ToString());
                if (added)
                {
                    Posts.Add(post);
                }
            }
        });
    }

    /// <summary>
    /// 后台增量同步文章列表
    /// </summary>
    private async Task SyncInBackgroundAsync()
    {
        try
        {
            // 30秒节流防重复同步
            if (DateTime.Now - _lastSyncTime < TimeSpan.FromSeconds(MinSyncIntervalSeconds))
                return;

            await SyncIncrementalAsync();
            _lastSyncTime = DateTime.Now;

            if (Posts.Count == 0)
                StatusMessage = "暂无内容";
            else
                StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServiceAccountDetailViewModel] 后台同步失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 增量同步：基于 since 时间戳只请求新文章
    /// </summary>
    private async Task SyncIncrementalAsync()
    {
        if (_serviceAccountService == null) return;

        DateTime? since = null;
        if (_cacheService != null)
        {
            // 按账号隔离 since 游标：之前用全局 MAX，多服务号切换时较旧账号的新文章被过滤掉，永远同步不到。
            since = await Task.Run(() => _cacheService.GetLatestCachedPublishedAtAsync(ServiceAccountId));
        }

        var newItems = new List<ServiceAccountPostListItem>();

        await Task.Run(async () =>
        {
            var response = await _serviceAccountService.GetPostsAsync(ServiceAccountId, 50, 0, since);
            if (response != null && response.Count > 0)
            {
                foreach (var post in response)
                {
                    bool added;
                    lock (_displayedPostIdsLock) added = _displayedPostIds.Add(post.Id.ToString());
                    if (added)
                    {
                        newItems.Add(post);
                    }
                }

                // 写入本地缓存
                if (_cacheService != null)
                {
                    await _cacheService.UpdateCacheAsync(response);
                }
            }
        });

        // UI 操作在主线程执行
        _uiThreadDispatcher?.Post(() =>
        {
            for (int i = newItems.Count - 1; i >= 0; i--)
            {
                Posts.Insert(0, newItems[i]);
            }
            if (Posts.Count > 0)
                StatusMessage = string.Empty;
        });
    }

    [RelayCommand]
    private async Task FollowAsync()
    {
        if (_serviceAccountService == null || ServiceAccountId == Guid.Empty) return;

        try
        {
            var result = await Task.Run(() => _serviceAccountService.FollowAsync(ServiceAccountId));
            if (result != null)
            {
                IsFollowed = true;
                FollowerCount++;
            }
            else
            {
                StatusMessage = "关注失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"关注失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UnfollowAsync()
    {
        if (_serviceAccountService == null || ServiceAccountId == Guid.Empty) return;

        try
        {
            var ok = await Task.Run(() => _serviceAccountService.UnfollowAsync(ServiceAccountId));
            if (ok)
            {
                IsFollowed = false;
                FollowerCount = Math.Max(0, FollowerCount - 1);
            }
            else
            {
                StatusMessage = "取消关注失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"取消关注失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await SyncIncrementalAsync();
            _lastSyncTime = DateTime.Now;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void GoToPostDetail(Guid postId)
    {
        NavigateToPostDetailRequested?.Invoke(this, new ServiceAccountPostNavigationEventArgs { PostId = postId });
    }
}