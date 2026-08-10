using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;

namespace Acme.Mobile.Mvvm.ViewModels;

public partial class DiscoveryViewModel : NavigableViewModelBase
{
    [ObservableProperty]
    private string _title = "发现";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 导航请求事件
    /// </summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public DiscoveryViewModel(IUIThreadDispatcher? uiThreadDispatcher = null)
    {
        _uiThreadDispatcher = uiThreadDispatcher;
    }

    [RelayCommand]
    private Task GoToMomentsAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("MomentsPage"));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task GoToVideoChannelsAsync()
    {
        NavigationRequested?.Invoke(this, NavigationRequestedEvent.To("VideoChannelsPage"));
        return Task.CompletedTask;
    }
}