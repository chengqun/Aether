namespace Acme.Shell;

/// <summary>
/// 连接器 ViewModel 标记接口（UI 无关）。具体连接器（如 CTP）由各租户宿主实现并注入；
/// ExpertsViewModel 通过此接口可选持有，无连接器时连接器 tab 隐藏。
/// </summary>
public interface IConnectorViewModel
{
    /// <summary>启动期刷新连接状态（切到连接器 tab 时调用）。</summary>
    Task RefreshAsync();
}
