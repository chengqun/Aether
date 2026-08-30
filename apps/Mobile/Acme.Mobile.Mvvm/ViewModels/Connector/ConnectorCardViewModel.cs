using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels.Connector;

/// <summary>
/// 手机端连接器卡片抽象基类（UI 无关，CommunityToolkit.Mvvm）。
///
/// 镜像桌面 <c>Acme.Desktop.Mvvm.ConnectorCardViewModel</c> 的形状，但手机端不引 Avalonia——
/// 在本库起一套并行抽象（"复用模式不复用代码"）。连接器页用 <c>BindableLayout</c> + 通用 DataTemplate
/// 数据驱动渲染 <see cref="ConnectorViewModel.Cards"/>：加卡 = 加一个子类 + DI 注册，零 XAML 改动
///（对齐桌面 <c>ExpertsView</c> 的 <c>ItemsControl</c>+<c>WrapPanel</c> 数据驱动卡片网格）。
///
/// 与桌面基类的差异：手机端无 <c>SyncCommand</c>——token 上传（"同步到服务器"）是桌面端连接器的活，
/// 手机端只「从服务器拉」，故无同步按钮。
/// </summary>
public abstract partial class ConnectorCardViewModel : ObservableObject
{
    /// <summary>emoji 图标（如 "🗄️" / "🔢"）。</summary>
    public string Icon { get; init; } = "";
    /// <summary>图标徽章背景色（hex，如 "#FFF3E0"）。当前手机 DataTemplate 用中性徽章底，本字段保留 API 对齐桌面。</summary>
    public string IconBackground { get; init; } = "#E5E7EB";
    /// <summary>卡片标题。</summary>
    public string Title { get; init; } = "";
    /// <summary>卡片描述。</summary>
    public string Description { get; init; } = "";

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private bool _isPrimaryEnabled = true;
    [ObservableProperty] private bool _isPrimaryInProgress;
    [ObservableProperty] private string _primaryButtonText = "";

    /// <summary>主按钮命令。auth 卡由子类在 ctor 里设为 LoginAsync（从服务器同步）；
    /// 需导航到设置子页的卡（如 MySQL）由 <see cref="ConnectorViewModel"/> 接管为「导航到 <see cref="SettingsPageRoute"/>」。</summary>
    public IRelayCommand? PrimaryCommand { get; set; }

    /// <summary>非空时 <see cref="ConnectorViewModel"/> 构造期把该卡 PrimaryCommand 接管为「导航到该路由」
    ///（如 "MySqlSettingsPage"）。auth 卡返回 null，自管 PrimaryCommand。</summary>
    public virtual string? SettingsPageRoute => null;

    /// <summary>进入连接器页/页面激活时刷新状态（试连/查认证态）。</summary>
    public abstract Task RefreshAsync();
}
