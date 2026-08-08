using System.Globalization;
using Acme.Persistence;
using Acme.Securities.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Securities.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ISyncMetadataStore"/>. Table <c>sync_metadata</c>
/// (key, last_sync_time). Used by the security client sync for incremental pulls.
/// </summary>
public sealed class SqliteSyncMetadataStore : SqliteStoreBase, ISyncMetadataStore
{
    private const string TableName = "sync_metadata";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (key TEXT PRIMARY KEY, last_sync_time TEXT NOT NULL)";

    public SqliteSyncMetadataStore(string connectionString) : base(connectionString) { }

    public static SqliteSyncMetadataStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<DateTime?> GetLastSyncTimeAsync(string key, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT last_sync_time FROM {TableName} WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : DateTime.Parse((string)result, CultureInfo.InvariantCulture);
    }

    public async Task SetLastSyncTimeAsync(string key, DateTime time, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO {TableName} (key, last_sync_time) VALUES (@k, @t) " +
                          $"ON CONFLICT(key) DO UPDATE SET last_sync_time=excluded.last_sync_time";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@t", time.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
