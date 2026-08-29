using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 通用 labs 资产列表页（AI 设置 → 某类型入口）。接收导航参数 <c>type</c>（agent/workflow/skill/mcp/knowledge），
/// 显示该类型列表 + 「＋新建」按钮 + 点列表项进编辑页。5 类共用一页。
/// </summary>
public partial class AssetListPage : ContentPage, IQueryAttributable
{
    private readonly AssetListViewModel _vm;

    public AssetListPage(AssetListViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _vm.SetParameters(query);

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }
}
