using System.ComponentModel;
using System.Linq;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// AI tab 页（豆包式）：对话优先 + 左侧抽屉历史。托管常驻 <see cref="AgentChatSurface"/>
/// （BindingContext = <see cref="AiChatViewModel.ChatSurface"/>）。抽屉开合由 code-behind
/// 监听 <see cref="AiChatViewModel.IsDrawerOpen"/> 做 <c>TranslateTo</c>/<c>FadeTo</c> 动画。
/// </summary>
public partial class AiChatPage : ContentPage
{
    private readonly AiChatViewModel _vm;
#if IOS
    private UIKit.UIScreenEdgePanGestureRecognizer? _backEdgePan;
#endif

    public AiChatPage(AiChatViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        Surface.BindingContext = _vm.ChatSurface;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if IOS
        // AI 页是全屏 tab（非推送页），无原生 swipe-back。挂一个左边缘右滑手势，复刻 iOS 推送页
        // 的边缘滑动返回：触发与 ‹ 按钮相同的 CloseAi 路径。仅左边缘激活，不干扰消息区滚动。
        if (Handler?.PlatformView is UIKit.UIView pv && _backEdgePan == null)
        {
            _backEdgePan = new UIKit.UIScreenEdgePanGestureRecognizer(HandleBackEdgePan)
            {
                Edges = UIKit.UIRectEdge.Left
            };
            pv.AddGestureRecognizer(_backEdgePan);
        }
#endif
    }

#if IOS
    private void HandleBackEdgePan()
    {
        if (_backEdgePan?.State != UIKit.UIGestureRecognizerState.Recognized) return;
        // 抽屉开着时，边缘右滑先关抽屉（而非返回），避免误退
        if (_vm.IsDrawerOpen)
        {
            _vm.IsDrawerOpen = false;
            return;
        }
        _vm.CloseAiCommand.Execute(null);
    }
#endif

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiChatViewModel.IsDrawerOpen))
        {
            await AnimateDrawerAsync(_vm.IsDrawerOpen);
        }
    }

    /// <summary>抽屉滑入/滑出 + 遮罩淡入/淡出。遮罩 InputTransparent 跟随开合，避免关闭态拦截主区点击。</summary>
    private async Task AnimateDrawerAsync(bool open)
    {
        Backdrop.InputTransparent = !open;
        // 抽屉关闭时穿透触摸（平移不改变命中测试区，否则挡住右侧发送按钮/LLM chip）
        Drawer.InputTransparent = !open;
        if (open)
        {
            Drawer.IsVisible = true;
            Backdrop.IsVisible = true;
        }
        // 抽屉从右横向平移 + 遮罩透明度
        await Task.WhenAll(
            Drawer.TranslateTo(open ? 0 : 300, 0, 200, Easing.SinOut),
            Backdrop.FadeTo(open ? 1 : 0, 200));
        if (!open)
        {
            // 关闭后隐藏遮罩；抽屉保留可见（已移出屏幕）以便下次动画无闪烁
            Backdrop.IsVisible = false;
        }
    }

    private async void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ObservableConversation item) return;
        // 清除选择，便于再次点同一项
        ((CollectionView)sender!).SelectedItem = null;
        await _vm.SelectHistoryAsync(item);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
        // 首次显示时同步抽屉初始位（关闭态）
        if (!_vm.IsDrawerOpen)
        {
            Backdrop.IsVisible = false;
            Backdrop.Opacity = 0;
            Backdrop.InputTransparent = true;
        }
    }

    protected override async void OnDisappearing()
    {
        // 切走 tab → 取消在跑流式（不耗电）；不动抽屉状态/历史
        await _vm.OnDeactivatedAsync();
        base.OnDisappearing();
    }
}
