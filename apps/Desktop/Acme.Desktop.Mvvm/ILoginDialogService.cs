namespace Acme.Shell;

/// <summary>弹出登录对话框，返回是否登录成功。UI 无关接口；实现由各 UI 框架提供。</summary>
public interface ILoginDialogService
{
    Task<bool> ShowLoginAsync();
}
