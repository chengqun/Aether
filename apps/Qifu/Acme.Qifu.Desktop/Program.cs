using Avalonia;
using System;
using System.IO;

namespace Acme.Qifu.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // 全局未处理异常日志：崩溃时把托管异常类型 + 堆栈写到 crash.log，便于定位（不再靠猜）。
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Aether-crash.log");
            var text = $"\n===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {source} =====\n" +
                       (ex is null ? "(no exception)" : ex.ToString()) + "\n";
            File.AppendAllText(path, text);
        }
        catch { /* 日志失败不阻断崩溃路径 */ }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
