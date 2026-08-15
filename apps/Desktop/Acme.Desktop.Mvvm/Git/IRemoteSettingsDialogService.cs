namespace Acme.Desktop.Mvvm.Git;

/// <summary>
/// 远程 git 凭证对话框服务（UI 无关端口）。让 <see cref="SourceControlViewModel"/>（Mvvm，不能引 Avalonia 窗口）
/// 触发「远程凭证」窗口（诊断系统 git credential helper 认证状态 + 测试连接）。平行于 <c>IConnectorSettingsDialogService</c>：
/// 同属「VM 触发弹窗、宿主实现」模式。凭证完全由系统 git 管理，窗口不存储任何 PAT。
/// </summary>
public interface IRemoteSettingsDialogService
{
    /// <summary>打开远程凭证对话框（模态，主窗口为父）。返回后调用方应刷新远程状态。</summary>
    Task ShowAsync();
}
