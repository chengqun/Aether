using Acme.Mobile.Mvvm.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Moments.Domain;
using Acme.Moments.Domain;
using Acme.Mobile.Mvvm.Abstractions;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class ContactMomentsViewModel : NavigableViewModelBase
{
    private readonly IMomentService? _momentService;
    private readonly IMomentCacheService? _momentCacheService;
    private bool _isLoaded;
    private readonly HashSet<string> _displayedMomentIds = new();

    [ObservableProperty]
    private string _title = "朋友圈";

    [ObservableProperty]
    private string _statusMessage = "加载中...";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _targetId = string.Empty;

    [ObservableProperty]
    private string _targetType = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    public ObservableCollection<MomentItem> Moments { get; } = new ObservableCollection<MomentItem>();

    public ContactMomentsViewModel(IMomentService? momentService = null,
                                   IMomentCacheService? momentCacheService = null,
                                   IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _momentService = momentService;
        _momentCacheService = momentCacheService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    /// <summary>
    /// 设置导航参数（由平台层调用）
    /// </summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query.TryGetValue("targetId", out var targetId))
            TargetId = targetId?.ToString() ?? string.Empty;

        if (query.TryGetValue("targetType", out var targetType))
            TargetType = targetType?.ToString() ?? string.Empty;

        if (query.TryGetValue("username", out var username))
            Username = username?.ToString() ?? string.Empty;

        Title = string.IsNullOrEmpty(Username) ? "朋友圈" : $"{Username}的朋友圈";
    }

    public override async Task OnActivatedAsync()
    {
        if (!string.IsNullOrEmpty(TargetId) && !_isLoaded)
        {
            _isLoaded = true;
            await LoadMomentsAsync();
        }
    }

    public void ResetLoadState()
    {
        _isLoaded = false;
        Moments.Clear();
        _displayedMomentIds.Clear();
    }

    private async Task LoadMomentsAsync()
    {
        try
        {
            // 1. 从缓存加载（瞬间完成）
            await LoadCachedMomentsAsync();

            StatusMessage = Moments.Count == 0 ? "暂无动态" : string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败: {ex.Message}";
        }

        // 2. 后台增量同步（不阻塞 UI）
        _ = SyncInBackgroundAsync();
    }

    private async Task LoadCachedMomentsAsync()
    {
        if (_momentCacheService == null || string.IsNullOrEmpty(TargetId)) return;

        var cached = await Task.Run(() => _momentCacheService.GetCachedMomentsAsync(TargetId));
        foreach (var dto in cached)
        {
            if (_displayedMomentIds.Add(dto.Id.ToString()))
            {
                Moments.Add(ToMomentItem(dto));
            }
        }
    }

    private async Task SyncInBackgroundAsync()
    {
        if (_momentService == null || string.IsNullOrEmpty(TargetId)) return;

        try
        {
            List<Moment> newItems = await Task.Run(async () =>
            {
                var moments = await _momentService.GetFriendsMomentsAsync(50, 0);
                var newDtos = new List<Moment>();
                if (moments != null && moments.Count > 0)
                {
                    foreach (var dto in moments)
                    {
                        if (_displayedMomentIds.Add(dto.Id.ToString()))
                        {
                            newDtos.Add(dto);
                        }
                    }

                    if (_momentCacheService != null)
                    {
                        foreach (var dto in moments)
                        {
                            if (string.IsNullOrEmpty(dto.TargetId))
                                dto.TargetId = TargetId;
                        }
                        await _momentCacheService.UpdateCacheAsync(moments);
                    }
                }
                return newDtos;
            });

            // UI 操作在主线程执行
            _uiThreadDispatcher?.Post(() =>
            {
                for (int i = newItems.Count - 1; i >= 0; i--)
                {
                    Moments.Insert(0, ToMomentItem(newItems[i]));
                }

                if (Moments.Count == 0)
                    StatusMessage = "暂无动态";
                else
                    StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ContactMomentsViewModel] 后台同步失败: {ex.Message}");
        }
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
            if (_momentService == null || string.IsNullOrEmpty(TargetId)) return;

            List<Moment> newItems = await Task.Run(async () =>
            {
                var moments = await _momentService.GetFriendsMomentsAsync(50, 0);
                var newDtos = new List<Moment>();
                if (moments != null && moments.Count > 0)
                {
                    foreach (var dto in moments)
                    {
                        if (_displayedMomentIds.Add(dto.Id.ToString()))
                        {
                            newDtos.Add(dto);
                        }
                    }

                    if (_momentCacheService != null)
                    {
                        foreach (var dto in moments)
                        {
                            if (string.IsNullOrEmpty(dto.TargetId))
                                dto.TargetId = TargetId;
                        }
                        await _momentCacheService.UpdateCacheAsync(moments);
                    }
                }
                return newDtos;
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
        finally
        {
            IsLoading = false;
        }
    }
}