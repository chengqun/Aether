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
/// 服务号信息流 ViewModel：聚合展示所有已关注服务号发布的文章
/// 缓存优先加载：先展示本地缓存，后台增量同步新数据
/// </summary>
public partial class ServiceAccountFeedViewModel : NavigableViewModelBase
{
    private readonly IServiceAccountService? _serviceAccountService;
    private readonly IServiceAccountCacheService? _cacheService;
    private readonly HashSet<string> _displayedPostIds = new();
    // _displayedPostIds 被 LoadCachedPostsAsync(主线程 dispatcher)/SyncIncrementalAsync(Task.Run 线程池)访问，
    // HashSet 非线程安全，下拉刷新与后台同步并发会结构损坏/重复插入，必须加锁。
    private readonly object _displayedPostIdsLock = new();
    private DateTime _lastSyncTime = DateTime.MinValue;
    private const int MinSyncIntervalSeconds = 30;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<ServiceAccountPostListItem> Posts { get; } = new();

    /// <summary>
    /// 导航到文章详情事件
    /// </summary>
    public event EventHandler<ServiceAccountPostNavigationEventArgs>? NavigateToPostDetailRequested;

    public ServiceAccountFeedViewModel(IServiceAccountService? serviceAccountService = null,
                                       IServiceAccountCacheService? cacheService = null,
                                       IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _serviceAccountService = serviceAccountService;
        _cacheService = cacheService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        await LoadPostsAsync();
    }

    /// <summary>
    /// 缓存优先加载：先展示本地缓存（秒开），后台增量同步
    /// </summary>
    private async Task LoadPostsAsync()
    {
        try
        {
            // 1. 从缓存加载（瞬间完成）
            await LoadCachedPostsAsync();

            // 缓存加载完就关闭转圈，用户立即可见
            StatusMessage = Posts.Count == 0 ? "暂无内容" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }

        // 2. 后台增量同步（不阻塞 UI）
        _ = SyncInBackgroundAsync();
    }

    /// <summary>
    /// 从本地 SQLite 缓存加载文章列表
    /// </summary>
    private async Task LoadCachedPostsAsync()
    {
        if (_cacheService == null) return;

        var cached = await Task.Run(() => _cacheService.GetCachedPostsAsync());
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
    /// 后台增量同步：基于 since 时间戳只请求新数据
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
            Debug.WriteLine($"[ServiceAccountFeed] 后台同步失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 增量同步：取最新缓存时间作为 since，只获取比缓存更新的文章
    /// </summary>
    private async Task SyncIncrementalAsync()
    {
        if (_serviceAccountService == null) return;

        DateTime? since = null;
        if (_cacheService != null)
        {
            since = await Task.Run(() => _cacheService.GetLatestCachedPublishedAtAsync());
        }

        var newItems = new List<ServiceAccountPostListItem>();

        await Task.Run(async () =>
        {
            var response = await _serviceAccountService.GetFollowedPostsAsync(50, 0, since);
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

        // UI 操作在主线程执行：新文章插入到顶部
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

/// <summary>
/// 服务号文章导航参数
/// </summary>
public class ServiceAccountPostNavigationEventArgs : EventArgs
{
    public Guid PostId { get; set; }
}