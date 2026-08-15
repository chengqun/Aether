namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// 远程 git 设置对话框服务（UI 无关端口）。让 <see cref="SourceControlViewModel"/>（Mvvm，不能引 Avalonia 窗口）
/// 触发「远程设置」窗口（GitHub 用户名 + PAT 配置）。平行于 <c>IConnectorSettingsDialogService</c>：
/// 同属「VM 触发弹窗、宿主实现」模式。窗口内写 <see cref="IPreferencesStore"/> 的 git.username/git.token。
/// </summary>
public interface IRemoteSettingsDialogService
{
    /// <summary>打开远程设置对话框（模态，主窗口为父）。返回后调用方应刷新远程状态。</summary>
    Task ShowAsync();
}
