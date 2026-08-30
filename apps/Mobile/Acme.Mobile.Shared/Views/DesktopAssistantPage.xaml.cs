using System.Collections.Specialized;
using System.ComponentModel;
using Acme.Mobile.Mvvm.Models;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 桌面助手页：手机远程控制桌面 agent 的独立功能入口。
/// 顶部「选择电脑」下拉 + 消息列表 + 输入栏。绑定独立 <see cref="DesktopAssistantViewModel"/>
/// （不复用社交聊天 <see cref="ChatDetailViewModel"/>）。
/// </summary>
public partial class DesktopAssistantPage : ContentPage
{
    private readonly DesktopAssistantViewModel _vm;

    public DesktopAssistantPage(DesktopAssistantViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;

        _vm.Messages.CollectionChanged += OnMessagesChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private async void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopAssistantViewModel.ConversationName))
            Title = _vm.ConversationName;
        else if (e.PropertyName == nameof(DesktopAssistantViewModel.IsPickerSheetOpen))
            await AnimatePickerSheetAsync(_vm.IsPickerSheetOpen);
    }

    /// <summary>「选择电脑」底部 sheet 开合：从 chip 处小幅淡入/微滑（镜像 AiChatPage 的选择器浮层）。
    /// sheet 锚消息区底部（chip 上沿），开时 TranslationY=14+Opacity=0 → 0/1，视觉上从 chip 处浮现展开。
    /// 关闭态 IsVisible=false（不渲染、不挡输入框）。平移不改变命中测试区，故开态须 InputTransparent=False。</summary>
    private async Task AnimatePickerSheetAsync(bool open)
    {
        const double popOffset = 14; // chip 处小幅浮现位移
        if (open)
        {
            PickerSheet.TranslationY = popOffset;
            PickerSheet.Opacity = 0;
            SheetBackdrop.Opacity = 0;
            PickerSheet.IsVisible = true;
            SheetBackdrop.IsVisible = true;
        }
        SheetBackdrop.InputTransparent = !open;
        PickerSheet.InputTransparent = !open;
        await Task.WhenAll(
            PickerSheet.TranslateTo(0, open ? 0 : popOffset, 180, Easing.SinOut),
            PickerSheet.FadeTo(open ? 1 : 0, 180),
            SheetBackdrop.FadeTo(open ? 1 : 0, 180));
        if (!open)
        {
            SheetBackdrop.IsVisible = false;
            PickerSheet.IsVisible = false;
        }
    }

    /// <summary>sheet 选项点击：转发到 VM SelectPickerItemCommand。
    /// BindableLayout 项模板的 TapGestureRecognizer Tapped 事件（非 Command RelativeSource 绑定，source-gen 下事件更稳）。
    /// sender 是被点的 Grid，其 BindingContext 即 PickerItem。</summary>
    private async void OnPickerItemTapped(object? sender, EventArgs e)
    {
        if (sender is not VisualElement { BindingContext: PickerItem item }) return;
        await _vm.SelectPickerItemCommand.ExecuteAsync(item);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = _vm.ConversationName;
        await _vm.OnActivatedAsync();
        await ScrollToBottomAsync();
        // 选择器浮层初始隐藏（关闭态不渲染、不挡输入）
        if (!_vm.IsPickerSheetOpen)
        {
            SheetBackdrop.IsVisible = false;
            SheetBackdrop.Opacity = 0;
            SheetBackdrop.InputTransparent = true;
        }
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnDeactivatedAsync();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ThrottledScrollToBottom();
    }

    /// <summary>
    /// 滚到底部——**关键：两段式（渲染追赶）**。
    /// 桌面端回复是非流式一次性到达的完整 markdown（≤300 字概览），Indiko MarkdownView 异步解析+排版
    /// （30ms 第一遍滚时 markdown 未必渲染完→拿到的 item 高度是占位值→按过期高度滚动→markdown 渲染后
    /// item 长高但滚动没更新→内容被裁「被挡住」）。
    /// 对照：手机 AI agent 对话页（AgentChatSurface）订阅 StreamingContentUpdated——流式逐 token 追加时
    /// 每次更新都重滚（OnStreamingContentUpdated→ThrottledScrollToBottom），总能追上最终高度，故不裁切。
    /// 桌面页无流式事件，故用固定延时补一次第二滚（等 markdown 渲染完再按真实高度对齐），镜像 agent 页
    /// 「内容更新后重滚」的行为。这正是两页「被挡住」差异的根因。
    /// </summary>
    private void ThrottledScrollToBottom()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Task.Delay(30);
                ScrollLastToEnd();
                // 第二遍：等 Indiko MarkdownView 异步渲染完成后按真实高度再滚一次。
                await Task.Delay(450);
                ScrollLastToEnd();
            }
            catch { }
        });
    }

    private void ScrollLastToEnd()
    {
        if (_vm.Messages.Count > 0)
        {
            var last = _vm.Messages[^1];
            MessagesList.ScrollTo(last, position: ScrollToPosition.End, animate: false);
        }
    }

    private async Task ScrollToBottomAsync()
    {
        try
        {
            // 同两段式：OnAppearing 载入历史时也要等 markdown 渲染完再对齐，否则历史最后一条（桌面回复）
            // 按占位高度滚动→渲染后被裁（「历史记录里面看会被挡住」即此路径）。
            await Task.Delay(50);
            ScrollLastToEnd();
            await Task.Delay(450);
            ScrollLastToEnd();
        }
        catch { }
    }
}
