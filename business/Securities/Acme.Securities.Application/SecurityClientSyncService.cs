using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Acme.AppAuth.Domain;
using Acme.Securities.Domain;

namespace Acme.Securities.Application;

public class SecurityClientSyncService : ISecurityClientSyncService
{
    private const string SyncTimeKey = "SecurityLastSyncTime";
    private const string SyncEndpoint = "api/app/security-client-sync/updates";

    private readonly ISecurityRepository _securityRepository;
    private readonly IAppServerHttpClient _httpClientService;
    private readonly ISyncMetadataStore _syncMetadataStore;
    private readonly ISyncLogger _logger;

    public SecurityClientSyncService(
        ISecurityRepository securityRepository,
        IAppServerHttpClient httpClientService,
        ISyncMetadataStore syncMetadataStore,
        ISyncLogger logger)
    {
        _securityRepository = securityRepository;
        _httpClientService = httpClientService;
        _syncMetadataStore = syncMetadataStore;
        _logger = logger;
    }

    public async Task<SecuritySyncResult> SyncFromServerAsync(CancellationToken ct = default)
    {
        var lastSyncTime = await _syncMetadataStore.GetLastSyncTimeAsync(SyncTimeKey, ct);
        _logger.LogInformation("Security sync starting, lastSyncTime={LastSyncTime}", lastSyncTime);

        try
        {
            var endpoint = lastSyncTime.HasValue
                ? $"{SyncEndpoint}?after={Uri.EscapeDataString(lastSyncTime.Value.ToString("o"))}"
                : SyncEndpoint;

            _logger.LogDebug("Security sync GET {Endpoint}", endpoint);
            var result = await _httpClientService.GetAsync<SecuritySyncResponseDto>(endpoint, ct);

            if (result == null)
            {
                _logger.LogWarning("Security sync returned no data");
                return new SecuritySyncResult { LastSyncTime = lastSyncTime };
            }

            if (result.Securities == null || result.Securities.Count == 0)
            {
                _logger.LogInformation("Security sync returned empty securities list");
                return new SecuritySyncResult { LastSyncTime = lastSyncTime };
            }

            _logger.LogInformation("Security sync received {Count} securities, IsFullSync={IsFullSync}, ServerTime={ServerTime}",
                result.Securities.Count, result.IsFullSync, result.ServerTime);

            var securities = result.Securities.Select(dto =>
                new Security(0, dto.Code, dto.Name, dto.Type, dto.Exchange, dto.BaseCode, dto.UpdatedAt)).ToList();

            // ISecurityRepository 是本地 SQLite 深链（取消无意义，已显式延后），故不转发 ct。
            await _securityRepository.AddRangeAsync(securities, 1000);

            await _syncMetadataStore.SetLastSyncTimeAsync(SyncTimeKey, result.ServerTime, ct);

            _logger.LogInformation("Security sync completed, synced {Count} securities, saved lastSyncTime={ServerTime}",
                result.Securities.Count, result.ServerTime);

            return new SecuritySyncResult
            {
                SyncedCount = result.Securities.Count,
                IsFullSync = result.IsFullSync,
                LastSyncTime = result.ServerTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Security sync failed");
            return new SecuritySyncResult { LastSyncTime = lastSyncTime };
        }
    }
}
