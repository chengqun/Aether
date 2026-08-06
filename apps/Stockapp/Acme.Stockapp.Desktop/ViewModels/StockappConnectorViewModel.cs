using Acme.Shell;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// Stockapp 复合连接器：实现 <see cref="IConnectorViewModel"/>，内部并列两张卡片——
/// <see cref="Ctp"/>（CTP 期货）+ <see cref="Ths"/>（同花顺股票）。注入 ExpertsViewModel 的连接器 tab，
/// 由共享 ItemsControl + WrapPanel 数据驱动渲染（<see cref="Cards"/>）。Refresh 逐张刷新。
/// </summary>
public sealed class StockappConnectorViewModel : IConnectorViewModel
{
    public StockappConnectorViewModel(CtpConnectorViewModel ctp, ThsConnectorViewModel ths)
    {
        Ctp = ctp;
        Ths = ths;
        Cards = [ctp, ths];
    }

    /// <summary>CTP 期货连接器卡片。</summary>
    public CtpConnectorViewModel Ctp { get; }

    /// <summary>同花顺股票连接器卡片。</summary>
    public ThsConnectorViewModel Ths { get; }

    /// <summary>连接器卡片列表（共享面板数据驱动渲染）。</summary>
    public IReadOnlyList<ConnectorCardViewModel> Cards { get; }

    /// <summary>切到连接器 tab 时逐张刷新状态。</summary>
    public async Task RefreshAsync()
    {
        await Ctp.RefreshAsync();
        await Ths.RefreshAsync();
    }
}
