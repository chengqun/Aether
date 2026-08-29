using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// Agent 新建/编辑表单页（AI 设置 → 管理 AI 助手）。绑定 <see cref="AgentEditViewModel"/>。
/// 接收 Shell 导航参数 <c>agentId</c>（Guid 字符串，可缺省=新建）→ VM <see cref="AgentEditViewModel.SetParameters"/>。
/// </summary>
public partial class AgentEditPage : ContentPage, IQueryAttributable
{
    private AgentEditViewModel? _vm;

    public AgentEditPage(AgentEditViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        _vm.NavigationRequested += OnNavigationRequested;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _vm?.SetParameters(query);

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
