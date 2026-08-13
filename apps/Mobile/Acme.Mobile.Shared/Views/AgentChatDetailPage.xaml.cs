using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// 推送式 AI 聊天详情页（Shell 子页，tab bar 隐藏）。瘦身后仅托管 <see cref="AgentChatSurface"/>。
/// 5 个入口（聊天列表/picker/股票联系人/agent 联系人）经 Shell <c>GoToAsync("AgentChatDetailPage?...")</c> 推送，
/// <see cref="IQueryAttributable.ApplyQueryAttributes"/> → VM <see cref="AgentChatDetailViewModel.SetParameters"/>。
/// </summary>
public partial class AgentChatDetailPage : ContentPage, IQueryAttributable
{
    private AgentChatDetailViewModel? _vm;

    public AgentChatDetailPage(AgentChatDetailViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
        Surface.BindingContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query) => _vm?.SetParameters(query);

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentChatDetailViewModel.AgentName))
            Title = _vm?.AgentName ?? "AI 助手";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_vm != null)
        {
            // Stage 1：强制文字输入模式（语音识别为 stub）
            _vm.IsVoiceMode = false;
            Title = _vm.AgentName;
            await _vm.OnActivatedAsync();
        }
        await Surface.ScrollToBottomAsync();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        if (_vm != null) await _vm.OnDeactivatedAsync();
    }
}
