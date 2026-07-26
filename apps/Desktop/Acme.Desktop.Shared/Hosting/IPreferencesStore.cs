namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 轻量键值偏好存储（客户端 UI 状态，单用户）。用于跨重启保留选中 agent、召唤列表等。
/// </summary>
public interface IPreferencesStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}
