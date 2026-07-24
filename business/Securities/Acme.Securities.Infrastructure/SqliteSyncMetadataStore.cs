using System.Globalization;
using Acme.Securities.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Securities.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ISyncMetadataStore"/>. Table <c>sync_metadata</c>
/// (key, last_sync_time). Used by the security client sync for incremental pulls.
/// </summary>
public sealed class SqliteSyncMetadataStore : ISyncMetadataStore
{
    private const string TableName = "sync_metadata";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (key TEXT PRIMARY KEY, last_sync_time TEXT NOT NULL)";

    private readonly string _connectionString;
    private int _initialized;

    public SqliteSyncMetadataStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public static SqliteSyncMetadataStore ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<DateTime?> GetLastSyncTimeAsync(string key, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT last_sync_time FROM {TableName} WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : DateTime.Parse((string)result, CultureInfo.InvariantCulture);
    }

    public async Task SetLastSyncTimeAsync(string key, DateTime time, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {TableName} (key, last_sync_time) VALUES (@k, @t) " +
                          $"ON CONFLICT(key) DO UPDATE SET last_sync_time=excluded.last_sync_time";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@t", time.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using (var wal = conn.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; await wal.ExecuteNonQueryAsync(ct); }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { Interlocked.Exchange(ref _initialized, 0); throw; }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var p = conn.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000;";
        await p.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
