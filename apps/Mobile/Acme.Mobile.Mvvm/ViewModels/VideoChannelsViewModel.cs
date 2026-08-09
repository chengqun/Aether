using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Video.Domain;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Models;
using VideoItem = Acme.Mobile.Mvvm.Models.VideoItem;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class VideoChannelsViewModel : NavigableViewModelBase
{
    private readonly IVideoFeedService? _videoFeedService;
    private int _offset = 0;
    private bool _isLoadingMore = false;
    private readonly HashSet<string> _displayedVideoIds = new();

    public event Action<string?>? VideoChanged;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "加载中...";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private int _currentIndex = -1;

    [ObservableProperty]
    private VideoItem? _currentVideo;

    public ObservableCollection<VideoItem> Videos { get; } = new();

    public bool HasPrevious => CurrentIndex > 0;
    public bool HasNext => CurrentIndex < Videos.Count - 1;

    public VideoChannelsViewModel(IVideoFeedService? videoFeedService = null,
                                  IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _videoFeedService = videoFeedService;
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    partial void OnCurrentIndexChanged(int value)
    {
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        UpdateCurrentVideo();
    }

    private void UpdateCurrentVideo()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Videos.Count)
        {
            CurrentVideo = Videos[CurrentIndex];
            VideoChanged?.Invoke(CurrentVideo.VideoUrl);
        }
    }

    public override async Task OnActivatedAsync()
    {
        if (Videos.Count == 0)
        {
            await LoadInitialFeedAsync();
        }
        else
        {
            UpdateCurrentVideo();
        }
    }

    public override async Task OnDeactivatedAsync()
    {
        await Task.CompletedTask;
    }

    public async Task NextVideoAsync()
    {
        if (CurrentIndex < Videos.Count - 1)
        {
            CurrentIndex++;
        }
        else
        {
            await LoadMoreVideosAsync();
            if (CurrentIndex < Videos.Count - 1)
                CurrentIndex++;
        }
    }

    public async Task PreviousVideoAsync()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
        }
    }

    [RelayCommand]
    private async Task RetryLoadAsync()
    {
        _offset = 0;
        CurrentIndex = -1;
        CurrentVideo = null;
        Videos.Clear();
        _displayedVideoIds.Clear();
        await LoadInitialFeedAsync();
    }

    private async Task LoadInitialFeedAsync()
    {
        IsLoading = true;
        HasError = false;
        StatusMessage = "加载中...";

        try
        {
            if (_videoFeedService == null)
            {
                StatusMessage = "服务未初始化";
                return;
            }

            var feed = await Task.Run(() => _videoFeedService.GetVideoFeedAsync(20, 0));
            if (feed != null && feed.Count > 0)
            {
                _offset = feed.Count;
                foreach (var dto in feed)
                {
                    if (_displayedVideoIds.Add(dto.Id.ToString()))
                    {
                        Videos.Add(ToVideoItem(dto));
                    }
                }
                if (Videos.Count > 0)
                {
                    CurrentIndex = 0;
                }
                StatusMessage = string.Empty;
            }
            else
            {
                HasError = true;
                ErrorMessage = "暂无视频";
                StatusMessage = ErrorMessage;
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = ex.Message;
            StatusMessage = $"加载失败: {ex.Message}";
            Debug.WriteLine($"[VideoChannelsViewModel] LoadInitialFeedAsync: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadMoreVideosAsync()
    {
        if (_isLoadingMore || _videoFeedService == null)
            return;

        _isLoadingMore = true;
        try
        {
            var more = await Task.Run(() => _videoFeedService.GetMoreVideosAsync(20, _offset));
            if (more != null && more.Count > 0)
            {
                _offset += more.Count;
                foreach (var dto in more)
                {
                    if (_displayedVideoIds.Add(dto.Id.ToString()))
                    {
                        Videos.Add(ToVideoItem(dto));
                    }
                }
                OnPropertyChanged(nameof(HasNext));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoChannelsViewModel] LoadMoreVideosAsync: {ex.Message}");
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    private static VideoItem ToVideoItem(Acme.Video.Domain.VideoItem dto) => new()
    {
        Id = dto.Id.ToString(),
        Title = dto.Title,
        VideoUrl = dto.VideoUrl,
        CoverUrl = dto.CoverUrl,
        Author = dto.Author,
        Duration = dto.Duration
    };
}