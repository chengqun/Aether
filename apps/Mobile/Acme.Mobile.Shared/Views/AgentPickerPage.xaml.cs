using System.Linq;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// Agent 选择页：tap agent → VM 发布 <see cref="Acme.Mobile.Mvvm.Events.AgentPickedEvent"/>
/// （AI tab 在内嵌聊天面板内开新对话）→ 本页 pop 回 AI tab。
/// </summary>
public partial class AgentPickerPage : ContentPage
{
    private readonly AgentPickerViewModel _vm;

    public AgentPickerPage(AgentPickerViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not AgentPickItem item) return;
        // 清除选择，便于再次进入时仍可点同一项
        ((CollectionView)sender!).SelectedItem = null;
        await _vm.PickAgentAsync(item.Name);
        // pop 回 AI tab（VM 已发事件，AI tab 的 AiChatViewModel 订阅接收 → surface 内开新对话）
        await Shell.Current.GoToAsync("..");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.OnActivatedAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
    }
}
