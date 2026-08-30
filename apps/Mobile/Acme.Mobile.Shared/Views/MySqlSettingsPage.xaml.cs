using Acme.Mobile.Mvvm.ViewModels.Connector;

namespace Acme.Mobile.Shared.Views;

/// <summary>
/// MySQL 连接配置子页（共享）。表单绑同一 <see cref="MySqlConnectorCard"/> 单例（与连接器卡片共享状态）。
/// 进入时 <see cref="MySqlConnectorCard.RefreshAsync"/> 试连确认（曾配置过）。
/// </summary>
public partial class MySqlSettingsPage : ContentPage
{
    private readonly MySqlConnectorCard _card;

    public MySqlSettingsPage(MySqlConnectorCard card)
    {
        InitializeComponent();
        _card = card;
        BindingContext = _card;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _card.RefreshAsync();
    }
}
