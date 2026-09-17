using Acme.Desktop.Shared.Hosting;
using Acme.Desktop.Shared.Telemetry;
using Avalonia;

namespace Acme.Stockapp.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 崩溃遥测（DI 之前就要能接住异常）：全局 handler + 本地 crash.db 队列 + 非干净关机墓碑扫描。
        // 登录后由 CrashReportUploader 批量上报服务端。取代旧的 ~/Desktop/Aether-crash.log 裸 append。
        CrashRecorder.Initialize("stockapp", TenantDataPaths.GetTenantDir("Stockapp"));

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
