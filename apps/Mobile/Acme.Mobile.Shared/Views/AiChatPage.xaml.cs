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

    public AiChatPage(AiChatViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        Surface.BindingContext = _vm.ChatSurface;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.NavigationRequested += OnNavigationRequested;
    }

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

    /// <summary>LLM chip 点击：弹 ActionSheet 选 LLM（"默认"=回退平台默认；与桌面端全局默认语义一致）。</summary>
    private async void OnPickLlm(object? sender, EventArgs e)
    {
        var configs = _vm.LlmConfigs;
        if (configs == null || configs.Count == 0)
        {
            await DisplayAlert("提示", "暂无可选 LLM 配置，请先在「我 → LLM 配置」中添加。", "知道了");
            return;
        }

        // 破坏按钮选项："使用默认" 排第一（选它=回退默认），其余为各配置名。
        var names = new List<string> { "使用默认" };
        names.AddRange(configs.Select(c => c.Name));
        var choice = await DisplayActionSheet("选择 LLM", "取消", null, names.ToArray());
        if (choice == null || choice == "取消") return;

        if (choice == "使用默认")
        {
            _vm.SetLlm(null);
            return;
        }
        var matched = configs.FirstOrDefault(c => c.Name == choice);
        if (matched != null) _vm.SetLlm(matched);
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
