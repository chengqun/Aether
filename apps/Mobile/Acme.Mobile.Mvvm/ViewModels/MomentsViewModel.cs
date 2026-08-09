using Acme.Mobile.Mvvm.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Moments.Domain;
using Acme.Moments.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class MomentsViewModel : NavigableViewModelBase
{
    private readonly IMomentService? _momentService;
    private readonly IMomentCacheService? _momentCacheService;
    private readonly HashSet<string> _displayedMomentIds = new();
    // _displayedMomentIds 被 LoadCachedMomentsAsync(主线程)/SyncIncrementalAsync(Task.Run 线程池)/
    // PublishMomentAsync(主线程) 三处访问，HashSet 非线程安全，跨线程并发 Add 会结构损坏/抛异常，必须加锁。
    private readonly object _displayedMomentIdsLock = new();

    [ObservableProperty]
    private string _title = "朋友圈";

    [ObservableProperty]
    private string _statusMessage = "加载中...";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _newMomentContent = string.Empty;

    [ObservableProperty]
    private string? _newMomentImageUrl;

    /// <summary>
    /// 发布朋友圈请求事件（平台层需要显示输入对话框）
    /// </summary>
    public event EventHandler<PublishMomentRequestedEvent>? PublishMomentRequested;

    /// <summary>
    /// 导航请求事件（平台层执行实际导航）
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public ObservableCollection<MomentItem> Moments { get; } = new ObservableCollection<MomentItem>();

    public MomentsViewModel(IMomentService? momentService = null,
                            IMomentCacheService? momentCacheService = null,
                            IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _momentService = momentService;
        _momentCacheService = momentCacheService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    public override async Task OnActivatedAsync()
    {
        await LoadMomentsAsync();
    }

    private async Task LoadMomentsAsync()
    {
        try
        {
            // 1. 从缓存加载（瞬间完成）
            await LoadCachedMomentsAsync();

            // 缓存加载完就关闭转圈，用户立即可见
            StatusMessage = Moments.Count == 0 ? "暂无动态" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }

        // 2. 后台增量同步（不阻塞 UI）
        _ = SyncInBackgroundAsync();
    }

    private async Task SyncInBackgroundAsync()
    {
        try
        {
            await SyncIncrementalAsync();

            if (Moments.Count == 0)
                StatusMessage = "暂无动态";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MomentsViewModel] 后台同步失败: {ex.Message}");
        }
    }

    private async Task LoadCachedMomentsAsync()
    {
        if (_momentCacheService == null) return;

        var cached = await Task.Run(() => _momentCacheService.GetCachedMomentsAsync());
        foreach (var dto in cached)
        {
            bool added;
            lock (_displayedMomentIdsLock) added = _displayedMomentIds.Add(dto.Id.ToString());
            if (added)
            {
                Moments.Add(ToMomentItem(dto));
            }
        }
    }

    private async Task SyncIncrementalAsync()
    {
        if (_momentService == null) return;

        DateTime? since = null;
        if (_momentCacheService != null)
        {
            var sinceDto = await Task.Run(() => _momentCacheService.GetLatestCachedCreatedAtAsync());
            since = sinceDto?.LocalDateTime;
        }

        (List<Moment> newItems, List<Moment> allData) = await Task.Run(async () =>
        {
            var moments = await _momentService.GetFriendsMomentsAsync(50, 0, since);
            var newDtos = new List<Moment>();
            if (moments != null && moments.Count > 0)
            {
                foreach (var dto in moments)
                {
                    bool added;
                    lock (_displayedMomentIdsLock) added = _displayedMomentIds.Add(dto.Id.ToString());
                    if (added)
                    {
                        newDtos.Add(dto);
                    }
                }

                if (_momentCacheService != null)
                {
                    await _momentCacheService.UpdateCacheAsync(moments);
                }
            }
            return (newDtos, moments ?? new List<Moment>());
        });

        // UI 操作在主线程执行
        _uiThreadDispatcher?.Post(() =>
        {
            for (int i = newItems.Count - 1; i >= 0; i--)
            {
                Moments.Insert(0, ToMomentItem(newItems[i]));
            }
        });
    }

    private static DateTime EnsureLocalTime(DateTime dt)
    {
        return dt.Kind == DateTimeKind.Local
            ? dt
            : DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime();
    }

    private static MomentItem ToMomentItem(Moment dto) => new()
    {
        Id = dto.Id.ToString(),
        UserId = dto.UserId,
        Username = dto.Username,
        Avatar = dto.Username.Length > 0 ? dto.Username[0].ToString().ToUpper() : "?",
        Content = dto.Content,
        ImageUrl = dto.ImageUrl,
        Time = FormatTime(dto.CreatedAt),
        Likes = 0,
        Comments = 0
    };

    private static string FormatTime(DateTime createdAt)
    {
        var localTime = EnsureLocalTime(createdAt);
        var now = DateTime.Now;
        var diff = now - localTime;

        if (diff.TotalMinutes < 1)
            return "刚刚";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes}分钟前";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours}小时前";
        if (diff.TotalDays < 7)
            return $"{(int)diff.TotalDays}天前";
        return localTime.ToString("yyyy-MM-dd");
    }

    [RelayCommand]
    private async Task RefreshMomentsAsync()
    {
        IsLoading = true;
        try
        {
            await SyncIncrementalAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task AddMomentAsync()
    {
        // 请求平台层显示输入对话框
        PublishMomentRequested?.Invoke(this, new PublishMomentRequestedEvent());
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task NavigateToContactDetailAsync(MomentItem? moment)
    {
        if (moment == null || moment.UserId == Guid.Empty) return Task.CompletedTask;

        var parameters = new Dictionary<string, object>
        {
            { "id", moment.UserId.ToString() },
            { "name", moment.Username },
            { "initial", moment.Avatar },
            { "type", "好友" }
        };
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("ContactDetailPage", parameters));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 平台层在用户输入内容后调用此方法发布朋友圈
    /// </summary>
    public async Task PublishMomentAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        StatusMessage = "发布中...";
        IsLoading = true;

        try
        {
            if (_momentService == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }

            var created = await Task.Run(async () =>
            {
                var moment = await _momentService.CreateMomentAsync(content, null, "Text", "Friends");
                if (moment != null && _momentCacheService != null)
                {
                    await _momentCacheService.AddMomentAsync(moment);
                }
                return moment;
            });

            if (created != null)
            {
                StatusMessage = "发布成功";

                // 追加到列表（UI 操作在主线程执行）
                bool added;
                lock (_displayedMomentIdsLock) added = _displayedMomentIds.Add(created.Id.ToString());
                if (added)
                {
                    _uiThreadDispatcher?.Post(() =>
                    {
                        Moments.Insert(0, ToMomentItem(created));
                    });
                }
            }
            else
            {
                StatusMessage = "发布失败";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"发布失败: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>
/// 发布朋友圈请求事件
/// </summary>
public class PublishMomentRequestedEvent : EventArgs
{
}

public class MomentItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Avatar { get; set; } = "?";
    public string Content { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string Time { get; set; } = string.Empty;

    public bool HasImage => !string.IsNullOrEmpty(ImageUrl);

    private int _likes;
    public int Likes
    {
        get => _likes;
        set => SetProperty(ref _likes, value);
    }

    public int Comments { get; set; }
}