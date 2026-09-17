using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Desktop.Mvvm;
using Acme.DeviceConnect.Infrastructure;

namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// 崩溃遥测上报器：drain 本地 crash.db 队列 → <c>POST api/app/crash/record-crashes</c>（bearer 自动携带/刷新）。
/// <para>触发：启动立即一次 + 5 分钟周期 Timer；<see cref="IsLoggedInAsync"/> 为 false（本地 SQLite 查、零网络）
/// 时本地排队等下次——启动崩溃在首次登录后才补报，不丢。上报失败（PostAsync 返 null——任何失败含非 2xx/
/// 未配 BaseUrl）计 attempts 静默重试，10 次后由 PruneAsync 清掉（house style 同 token-sync 服务）。</para>
/// <para>设备名 best-effort：从 device.db（DeviceConnect）按租户取一次缓存——crash.db 记录时不回填
/// （崩溃路径绝不碰第二个库），drain 时补。</para>
/// </summary>
public sealed class CrashReportUploader : IDisposable
{
    private const string Endpoint = "api/app/crash/record-crashes";
    private const int BatchSize = 20;

    private readonly IAppServerHttpClient _httpClient;
    private readonly IAppAuthService _auth;
    private readonly SqliteDeviceConfigRepository? _deviceConfig;
    private readonly string _tenant;
    private readonly CrashReportStore _store;
    private System.Threading.Timer? _timer;

    private int _draining;
    private int _pruned;
    private int _deviceNameResolved;
    private string? _deviceNameCache;

    public CrashReportUploader(IAppServerHttpClient httpClient, IAppAuthService auth,
        SqliteDeviceConfigRepository? deviceConfig, string tenant, string crashDbPath)
    {
        _httpClient = httpClient;
        _auth = auth;
        _deviceConfig = deviceConfig;
        _tenant = tenant;
        _store = CrashReportStore.ForFile(crashDbPath);
    }

    /// <summary>启动：立即 drain 一次（含本地 Prune）+ 5 分钟周期。由 CrashTelemetryHost.Start 调用。</summary>
    public void Start()
    {
        DrainAsync().SafeFireAndForget();
        _timer = new System.Threading.Timer(
            _ => DrainAsync().SafeFireAndForget(),
            null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>外部触发立即 drain（如登录成功后想立刻补报积压——可选）。</summary>
    public void TriggerNow() => DrainAsync().SafeFireAndForget();

    private async Task DrainAsync()
    {
        if (Interlocked.Exchange(ref _draining, 1) != 0) return; // 单飞：周期 tick 与手动触发不并发
        try
        {
            // 本地清理不依赖登录态：超龄（30 天）/ 超尝试（10 次）/ 超量（>500 留新）直接删
            if (Interlocked.Exchange(ref _pruned, 1) == 0)
                await _store.PruneAsync().ConfigureAwait(false);

            if (!await _auth.IsLoggedInAsync().ConfigureAwait(false)) return;

            var batch = await _store.GetPendingAsync(BatchSize).ConfigureAwait(false);
            while (batch.Count > 0)
            {
                var deviceName = await ResolveDeviceNameAsync().ConfigureAwait(false);
                var request = new RecordCrashesRequest
                {
                    Crashes = batch.Select(r => new RecordCrashItemDto
                    {
                        App = r.App,
                        AppVersion = r.AppVersion,
                        Os = r.Os,
                        Source = r.Source,
                        Severity = (int)r.Severity,
                        ExceptionType = r.ExceptionType,
                        Message = r.Message,
                        Stack = r.Stack,
                        SessionId = r.SessionId,
                        OccurredAt = r.OccurredAt.UtcDateTime,
                        Count = r.Count,
                        DeviceName = deviceName,
                    }).ToList(),
                };

                var ack = await _httpClient.PostAsync<RecordCrashesAckDto>(Endpoint, request).ConfigureAwait(false);
                if (ack is null)
                {
                    // 失败计尝试——本轮到此（不连发重试），下个周期再试
                    await _store.IncrementAttemptsAsync(batch.Select(r => r.Id)).ConfigureAwait(false);
                    return;
                }
                await _store.DeleteReportsAsync(batch.Select(r => r.Id)).ConfigureAwait(false);
                if (batch.Count < BatchSize) break;
                batch = await _store.GetPendingAsync(BatchSize).ConfigureAwait(false);
            }
        }
        catch
        {
            /* 上报自身失败绝不影响宿主 */
        }
        finally
        {
            Volatile.Write(ref _draining, 0);
        }
    }

    private async Task<string?> ResolveDeviceNameAsync()
    {
        if (Interlocked.Exchange(ref _deviceNameResolved, 1) != 0) return _deviceNameCache;
        try
        {
            var config = _deviceConfig is null
                ? null
                : await _deviceConfig.GetByTenantAsync(_tenant).ConfigureAwait(false);
            _deviceNameCache = config?.DeviceName;
        }
        catch
        {
            _deviceNameCache = null; // best-effort——设备名缺失不影响上报
        }
        return _deviceNameCache;
    }

    public void Dispose() => _timer?.Dispose();
}
