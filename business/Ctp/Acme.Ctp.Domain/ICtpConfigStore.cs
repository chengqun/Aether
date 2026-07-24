namespace Acme.Ctp.Domain;

/// <summary>
/// Persists the CTP connection config locally. A concrete SQLite store is
/// provided by Infrastructure.
/// </summary>
public interface ICtpConfigStore
{
    Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(CtpConnectionConfig config, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
