using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels.Connector;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 连接器页（手机端）：数据驱动卡片列表 + 通用 DataTemplate。
/// 加卡 = 在宿主 DI 注册一个 <see cref="ConnectorCardViewModel"/> 子类，零 XAML 改动（对齐桌面 ExpertsView）。
/// 订阅 <see cref="ConnectorViewModel.NavigationRequested"/> 执行 Shell 导航（复用 MePage/AiSettingsPage 范式）。
/// </summary>
public partial class ConnectorPage : ContentPage
{
    private readonly ConnectorViewModel _vm;

    public ConnectorPage(ConnectorViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
        => await Shell.Current.GoToAsync(e.BuildNavigationUri());

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        await _vm.OnDeactivatedAsync();
    }
}
