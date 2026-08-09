using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 连接器卡片 ViewModel 基类（UI 无关）。每张卡片 = 一个可连接服务的状态徽章 + 主操作（"+"）+ 可选「同步」。
/// 各租户的连接器子面板派生此类，填入 Icon/Title/Description 并实现 <see cref="RefreshAsync"/>。
/// 连接器 tab 用 <c>ItemsControl</c> + <c>WrapPanel</c> 数据驱动渲染——加卡只需往
/// <see cref="IConnectorViewModel.Cards"/> 集合里加一项，零 XAML 改动，自动换行。
/// </summary>
public abstract partial class ConnectorCardViewModel : ObservableObject
{
    /// <summary>图标（emoji，与既有卡片风格一致）。</summary>
    public string Icon { get; set; } = "";

    /// <summary>图标徽章背景色（如 "#E8F5E9"）。</summary>
    public string IconBackground { get; set; } = "#E3F2FD";

    /// <summary>卡片标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>卡片副标题/描述。</summary>
    public string Description { get; set; } = "";

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "未连接";

    /// <summary>主操作（"+"按钮）是否可用（如登录中/忙碌时禁用）。</summary>
    [ObservableProperty] private bool _isPrimaryEnabled = true;

    /// <summary>是否显示「同步」按钮。</summary>
    [ObservableProperty] private bool _showSync;

    /// <summary>主操作命令（"+"按钮）：登录 / 打开设置等。各卡片在构造时自行赋值。</summary>
    public IRelayCommand? PrimaryCommand { get; protected set; }

    /// <summary>同步命令（可选；仅当 <see cref="ShowSync"/> 为 true 时显示）。</summary>
    public IRelayCommand? SyncCommand { get; protected set; }

    /// <summary>切到连接器 tab 时刷新连接状态。</summary>
    public abstract Task RefreshAsync();
}
