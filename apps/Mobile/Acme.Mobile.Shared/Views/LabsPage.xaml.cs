using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// labs 列表入口页（AI 设置 → labs）。列出 &lt;labs&gt; 各子目录文件，点开走 <see cref="LabsFileViewPage"/> 只读查看。
/// </summary>
public partial class LabsPage : ContentPage
{
    private readonly LabsViewModel _vm;

    public LabsPage(LabsViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnLoaded(object sender, EventArgs e)
    {
        if (_vm != null) await _vm.OnActivatedAsync();
    }
}
