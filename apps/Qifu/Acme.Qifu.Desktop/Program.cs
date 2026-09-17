using Acme.Desktop.Shared.Hosting;
using Acme.Desktop.Shared.Telemetry;
using Avalonia;

namespace Acme.Qifu.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // 崩溃遥测（DI 之前就要能接住异常）：全局 handler + 本地 crash.db 队列 + 非干净关机墓碑扫描。
        // 登录后由 CrashReportUploader 批量上报服务端。取代旧的 ~/Desktop/Aether-crash.log 裸 append。
        CrashRecorder.Initialize("qifu", TenantDataPaths.GetTenantDir("Qifu"));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
