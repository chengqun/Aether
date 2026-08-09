using System.Threading.Tasks;

namespace Acme.Mobile.Mvvm.Abstractions;

/// <summary>
/// 流式回复后台保活服务抽象。
/// Android: Foreground Service + WakeLock，屏幕关闭或切出 App 时保持进程存活。
/// iOS: BeginBackgroundTask，提供约 30 秒后台执行时间。
/// Avalonia/Windows: No-op（桌面应用不会被挂起）。
/// </summary>
public interface IStreamingBackgroundService
{
    /// <summary>
    /// 启动后台保护。在流式回复开始前调用。
    /// Android 上启动前台服务（显示通知）并获取 WakeLock。
    /// iOS 上开始后台任务。
    /// </summary>
    /// <param name="title">通知标题（如 "天机算筹"）</param>
    /// <param name="message">通知内容（如 "AI正在思考..."）</param>
    /// <returns>是否成功启动后台保护</returns>
    Task<bool> StartAsync(string title, string message);

    /// <summary>
    /// 更新通知文本（如从 "AI正在思考..." 变为 "AI正在回复..."）。
    /// 不支持通知的平台为 no-op。
    /// </summary>
    void UpdateMessage(string message);

    /// <summary>
    /// 停止后台保护。在流式回复结束后（无论成功或失败）调用。
    /// Android 上停止前台服务并释放 WakeLock。
    /// iOS 上结束后台任务。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 后台保护是否处于活跃状态。
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// 请求后台操作所需的运行时权限。
    /// Android 13+ 为 POST_NOTIFICATIONS。
    /// 权限被拒绝不阻塞流式回复（前台服务仍可运行，只是通知不可见）。
    /// </summary>
    Task<bool> RequestPermissionAsync();
}
