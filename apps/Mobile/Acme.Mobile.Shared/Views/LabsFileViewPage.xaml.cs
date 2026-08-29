using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// labs 文件只读查看页（列表项点开）。接收导航参数 <c>path</c>（相对 labs 根的路径），显示文件全文。
/// </summary>
public partial class LabsFileViewPage : ContentPage, IQueryAttributable
{
    private readonly LabsFileViewViewModel _vm;

    public LabsFileViewPage(LabsFileViewViewModel viewModel)
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

    private async void OnLoaded(object sender, EventArgs e)
    {
        if (_vm != null) await _vm.OnActivatedAsync();
    }
}
