using Acme.Shell;

namespace Acme.Stockapp.Desktop.ViewModels;

/// <summary>
/// Stockapp 复合连接器：实现 <see cref="IConnectorViewModel"/>，内部并列两个子连接器面板——
/// <see cref="Ctp"/>（CTP 期货）+ <see cref="Ths"/>（同花顺股票）。注入 ExpertsViewModel 的连接器 tab。
/// 两套连接逻辑互不搭界，UI 各自一张卡片（见 ConnectorView）。Refresh 统一刷新两者状态。
/// </summary>
public sealed class StockappConnectorViewModel : IConnectorViewModel
{
    public StockappConnectorViewModel(CtpConnectorViewModel ctp, ThsConnectorViewModel ths)
    {
        Ctp = ctp;
        Ths = ths;
    }

    /// <summary>CTP 期货连接器子面板。</summary>
    public CtpConnectorViewModel Ctp { get; }

    /// <summary>同花顺股票连接器子面板。</summary>
    public ThsConnectorViewModel Ths { get; }

    /// <summary>切到连接器 tab 时刷新两个子面板状态。</summary>
    public async Task RefreshAsync()
    {
        await Ctp.RefreshAsync();
        await Ths.RefreshAsync();
    }
}
