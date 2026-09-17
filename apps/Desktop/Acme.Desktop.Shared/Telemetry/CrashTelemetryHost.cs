using Acme.Desktop.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 崩溃遥测宿主接线：各租户 App 在 <c>DesktopApp.Services = Services</c> 之后调用
/// <see cref="Start"/>（在 App 的 Startup try/catch 包裹内）。启动 CrashReportUploader（立即 + 5 分钟周期 drain）。
/// <para>
/// E2E 验证钩子（env 门控，仅手动验证用）：
/// <c>AETHER_FORCE_NONFATAL=1</c> 记一条合成非致命（验证 队列→上报→Blazor 页 全链路）；
/// <c>AETHER_FORCE_CRASH=1</c> 在此处 throw——**必须逃出本方法**命中 App 的 Startup 包裹
/// （记 Fatal 后 rethrow）→ 进程崩 → 验证 Fatal 路径（重跑后本地排队、登录后补报）。
/// 故 FORCE_CRASH 检查在 try 之前，不被本地 catch 吞掉。
/// </para>
/// </summary>
public static class CrashTelemetryHost
{
    public static void Start(IServiceProvider services)
    {
        if (Environment.GetEnvironmentVariable("AETHER_FORCE_CRASH") == "1")
            throw new InvalidOperationException("AETHER_FORCE_CRASH E2E 验证：模拟启动期致命崩溃（应命中 Startup 包裹 → Fatal 记录 → 进程退出）");

        if (Environment.GetEnvironmentVariable("AETHER_FORCE_NONFATAL") == "1")
            CrashRecorder.Record(CrashSource.SafeFireAndForget, CrashSeverity.NonFatal,
                new InvalidOperationException("AETHER_FORCE_NONFATAL"), "崩溃遥测 E2E 验证：合成非致命记录");

        try
        {
            services.GetRequiredService<CrashReportUploader>().Start();
        }
        catch (Exception ex)
        {
            // uploader 启动失败（DI 缺失等）不阻断应用——记非致命即可
            CrashRecorder.Record(CrashSource.Startup, CrashSeverity.NonFatal, ex);
        }
    }
}
