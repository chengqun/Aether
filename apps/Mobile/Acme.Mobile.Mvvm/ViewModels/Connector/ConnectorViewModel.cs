using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels.Connector;

/// <summary>
/// 手机端「连接器」页 ViewModel（UI 无关，CommunityToolkit.Mvvm）。
///
/// 镜像桌面 <c>Acme.Desktop.Mvvm.IConnectorViewModel</c> 的数据驱动卡片设计：持一个
/// <see cref="Cards"/> 集合，由宿主经 DI 注入具体卡片（<see cref="IEnumerable{T}"/> of <see cref="ConnectorCardViewModel"/>），
/// VM 层不知具体类型——加卡 = 加一个 card VM 子类 + DI 注册，<see cref="ConnectorPage"/> 的通用 DataTemplate 自动渲染。
///
/// **导航接管**：卡片基类暴露 <see cref="ConnectorCardViewModel.SettingsPageRoute"/>（非空 = 该卡点主按钮要跳设置子页，
/// 如 MySQL 卡 → "MySqlSettingsPage"）。本 VM 构造时遍历 Cards，对 <c>SettingsPageRoute</c> 非空者把它的
/// <c>PrimaryCommand</c> 接管为「导航到该路由」（auth 卡的 <c>PrimaryCommand</c> 在子类 ctor 自设为 LoginAsync，不被接管）。
/// 导航经现有 <see cref="NavigationRequested"/> 事件 + <see cref="NavigationRequestedEvent.To"/>，ConnectorPage
/// code-behind 订阅 → <c>Shell.Current.GoToAsync</c>（复用 MePage/AiSettingsPage 范式，不新增 navigation 端口）。
/// </summary>
public sealed partial class ConnectorViewModel : NavigableViewModelBase
{
    private readonly ReadOnlyCollection<ConnectorCardViewModel> _cards;

    /// <summary>连接器卡片列表（只读视图，供 DataTemplate 渲染）。</summary>
    public ReadOnlyObservableCollection<ConnectorCardViewModel> Cards { get; }

    /// <summary>请求页面导航（宿主页面 code-behind 订阅，执行 Shell.GoToAsync）。对齐 MeViewModel/AiSettingsViewModel 的事件签名。</summary>
    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public ConnectorViewModel(IEnumerable<ConnectorCardViewModel> cards)
    {
        var list = cards?.ToList() ?? new List<ConnectorCardViewModel>();
        _cards = list.AsReadOnly();
        var oc = new ObservableCollection<ConnectorCardViewModel>(list);
        Cards = new ReadOnlyObservableCollection<ConnectorCardViewModel>(oc);

        // 接管需导航到设置子页的卡片的主按钮：auth 卡 SettingsPageRoute==null 不动（自管 LoginAsync）。
        foreach (var card in _cards)
        {
            var route = card.SettingsPageRoute;
            if (!string.IsNullOrWhiteSpace(route))
            {
                if (string.IsNullOrEmpty(card.PrimaryButtonText))
                    card.PrimaryButtonText = "配置连接";
                card.PrimaryCommand = new AsyncRelayCommand(() =>
                {
                    NavigationRequested?.Invoke(this, NavigationRequestedEvent.To(route));
                    return Task.CompletedTask;
                });
            }
        }
    }

    /// <summary>进入连接器页时刷新所有卡片状态（试连/查认证态）。</summary>
    public override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();
        foreach (var card in _cards)
        {
            await card.RefreshAsync();
        }
    }
}
