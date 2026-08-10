using Acme.Desktop.Workbench.Abstractions;
using Acme.Stockapp.Desktop.ViewModels;
using Acme.Stockapp.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Stockapp.Desktop;

/// <summary>
/// Stockapp 租户专属 panel 注册：期货/回测/股票页视图经 <see cref="IViewRegistryContributor"/> 注入工作台 view registry，
/// 让 Workbench 库不依赖租户程序集。VM 从 <see cref="StockappMainViewModel"/> 的类型化 Futures/Trading/StockTrade 属性取
/// （租户 shell VM 自持，不污染共享基类）。替代旧 App.axaml DataTemplate 映射。
/// </summary>
public sealed class StockappViewContributor : IViewRegistryContributor
{
    public void Contribute(IViewRegistry registry, IServiceProvider services)
    {
        registry.Register(PanelKeys.Ctp, () => new FuturesView(),
            () => services.GetRequiredService<StockappMainViewModel>().Futures);
        registry.Register(PanelKeys.Backtest, () => new TradingView(),
            () => services.GetRequiredService<StockappMainViewModel>().Trading);
        registry.Register(PanelKeys.StockTrade, () => new StockTradingView(),
            () => services.GetRequiredService<StockappMainViewModel>().StockTrade);
    }
}
