using Acme.Shell;
using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Acme.Desktop.Shared.Shell;

public partial class ChatView : UserControl
{
    private MainViewModelBase? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(ScrollToBottom);
    }

    public void ScrollToBottomOnShow()
    {
        Dispatcher.UIThread.Post(ScrollToBottom);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            var chatVm = _vm as IChatViewModel;
            chatVm?.Messages.CollectionChanged -= OnMessagesChanged;
        }
        _vm = DataContext as MainViewModelBase;
        if (_vm != null)
        {
            var chatVm = _vm as IChatViewModel;
            chatVm?.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Reset)
            ScrollToBottom();
    }

    private void ScrollToBottom() => MessagesScrollViewer?.ScrollToEnd();

    private void OnToolCardTapped(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Control control && control.DataContext is ChatMessageItem item)
            item.IsExpanded = !item.IsExpanded;
    }

    private void OnStreamingCardTapped(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (sender is Control control && control.DataContext is ChatMessageItem item)
            item.IsStreamingExpanded = !item.IsStreamingExpanded;
    }

    private void OnStreamingScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv) sv.ScrollToEnd();
    }

    private void OnDeviceConfigClick(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var settingsDialog = _vm.SettingsDialog;
        if (settingsDialog == null) return;
        _ = settingsDialog.ShowSettingsAsync();
    }
}
