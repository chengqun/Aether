using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class GroupDetailPage : ContentPage, IQueryAttributable
{
    private readonly GroupDetailViewModel _vm;

    public GroupDetailPage(GroupDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.PopAndRefreshRequested += OnPopAndRefreshRequested;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        string groupId = string.Empty;
        string groupName = string.Empty;
        string? description = null;

        if (query.TryGetValue("groupId", out var gid))
            groupId = gid?.ToString() ?? string.Empty;

        if (query.TryGetValue("groupName", out var gname))
            groupName = gname?.ToString() ?? string.Empty;

        if (query.TryGetValue("description", out var desc))
            description = desc?.ToString();

        _vm.SetParameters(groupId, groupName, description);
    }

    private async void OnNavigationRequested(object? sender, NavigationRequestedEvent e)
    {
        if (e.IsBack)
            await Shell.Current.Navigation.PopAsync();
        else
            await Shell.Current.GoToAsync(e.BuildNavigationUri());
    }

    private async void OnPopAndRefreshRequested(object? sender, EventArgs e)
    {
        await Shell.Current.Navigation.PopAsync();
    }

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
