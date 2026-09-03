namespace Acme.Qifu.Domain.Ports;

/// <summary>
/// qifu config/token cache store（桌面单用户，无 userId 维度）。
/// key-value：config_key（如 "token"/"sid"/"client_config"）→ config_json。
/// </summary>
public interface IQifuConfigCache
{
    Task<QifuConfigCacheItem?> GetAsync(string configKey, CancellationToken ct = default);
    Task UpsertAsync(string configKey, string configJson, CancellationToken ct = default);
    Task DeleteAsync(string configKey, CancellationToken ct = default);
}
