namespace Acme.Shell;

/// <summary>弹出设置对话框。UI 无关接口；实现由各 UI 框架提供。</summary>
public interface ISettingsDialogService
{
    Task ShowSettingsAsync();
}
