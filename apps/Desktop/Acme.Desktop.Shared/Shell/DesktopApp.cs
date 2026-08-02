using System;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 共享层访问租户 DI 容器的静态入口。各租户 App 在构建 ServiceProvider 后赋值 <see cref="Services"/>，
/// 供 shared 代码（如 MainWindow/NotesView 的布局持久化）解析 <c>IPreferencesStore</c> 等服务。
/// </summary>
public static class DesktopApp
{
    public static IServiceProvider Services { get; set; } = null!;
}
