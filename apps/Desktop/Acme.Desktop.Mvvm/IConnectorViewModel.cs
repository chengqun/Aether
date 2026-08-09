namespace Acme.Desktop.Mvvm;

/// <summary>
/// 连接器 ViewModel 接口（UI 无关）。具体连接器（如 CTP / 毓数）由各租户宿主实现并注入；
/// ExpertsViewModel 通过此接口可选持有，无连接器时连接器 tab 隐藏。
/// 连接器 tab 用 <see cref="Cards"/> 数据驱动渲染（ItemsControl + WrapPanel）。
/// </summary>
public interface IConnectorViewModel
{
    /// <summary>连接器卡片列表（数据驱动渲染）。各租户填充自己的卡片，加卡 = 加项。</summary>
    IReadOnlyList<ConnectorCardViewModel> Cards { get; }

    /// <summary>启动期刷新连接状态（切到连接器 tab 时调用），逐张刷新卡片。</summary>
    Task RefreshAsync();
}

/// <summary>
/// 连接器卡片设置对话框服务（UI 无关端口）。当卡片的「+」主操作是「打开设置窗口」时
/// （而非直接登录），卡片 VM 经此端口弹窗——VM 不直接引用 Avalonia 窗口类型。
/// 仅需要弹窗的租户（如 Stockapp 的 CTP/同花顺）实现并注册；直接登录的卡片（如 Qifu）不使用。
/// 平行于 <c>IApprovalDialogService</c>：同属「VM 触发弹窗、宿主实现」模式。
/// </summary>
public interface IConnectorSettingsDialogService
{
    /// <summary>为指定卡片打开其设置对话框（模态，主窗口为父）。</summary>
    Task ShowSettingsAsync(ConnectorCardViewModel card);
}
