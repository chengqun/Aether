using Acme.Mobile.Mvvm.ViewModels;
using CommunityToolkit.Maui.Views;

namespace Acme.Mobile.Shared.Views;

public partial class VideoChannelsPage : ContentPage
{
    private readonly VideoChannelsViewModel _vm;

    public VideoChannelsPage(VideoChannelsViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        // 上滑→下一个，下滑→上一个（TikTok 式翻页）
        var swipeUp = new SwipeGestureRecognizer { Direction = SwipeDirection.Up };
        swipeUp.Swiped += async (_, _) => await _vm.NextVideoAsync();
        var swipeDown = new SwipeGestureRecognizer { Direction = SwipeDirection.Down };
        swipeDown.Swiped += async (_, _) => await _vm.PreviousVideoAsync();

        Player.GestureRecognizers.Add(swipeUp);
        Player.GestureRecognizers.Add(swipeDown);

        // VM 切换 CurrentVideo 时把新 URL 推给播放器
        _vm.VideoChanged += OnVideoChanged;
    }

    private void OnVideoChanged(string? videoUrl)
    {
        if (string.IsNullOrEmpty(videoUrl))
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            Player.Source = MediaSource.FromUri(videoUrl);
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // 离开页面停止播放，释放音频/视频资源
        Player.Stop();
        _ = _vm.OnDeactivatedAsync();
    }
}
