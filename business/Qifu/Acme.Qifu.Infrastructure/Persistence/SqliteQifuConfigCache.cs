using System.Globalization;
using Acme.Persistence;
using Acme.Qifu.Domain.Ports;
using Microsoft.Data.Sqlite;

namespace Acme.Qifu.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IQifuConfigCache"/>（单用户，key-value，无 user_id 列）。
/// </summary>
public sealed class SqliteQifuConfigCache : SqliteStoreBase, IQifuConfigCache
{
    private const string TableName = "qifu_config_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "config_key TEXT NOT NULL PRIMARY KEY, " +
        "config_json TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL)";

    public SqliteQifuConfigCache(string connectionString) : base(connectionString) { }

    public static SqliteQifuConfigCache ForFile(string dbPath)
        => new(BuildConnectionString(dbPath));

    public async Task<QifuConfigCacheItem?> GetAsync(string configKey, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT config_json, cached_at FROM {TableName} WHERE config_key = @k";
        cmd.Parameters.AddWithValue("@k", configKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new QifuConfigCacheItem
        {
            ConfigKey = configKey,
            ConfigJson = reader.GetString(0),
            CachedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)
        };
    }

    public async Task UpsertAsync(string configKey, string configJson, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} (config_key, config_json, cached_at) VALUES (@k, @j, @t)";
        cmd.Parameters.AddWithValue("@k", configKey);
        cmd.Parameters.AddWithValue("@j", configJson);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string configKey, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE config_key = @k";
        cmd.Parameters.AddWithValue("@k", configKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>迁移：老表有 user_id 列 → 建新表 → 复制数据（按 config_key 去重保留最新）→ 换名。
    /// 新表/不存在时基类 <see cref="SqliteStoreBase.EnsureSchemaAsync"/> 已 CREATE IF NOT EXISTS，此处无需再做。</summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var checkCol = connection.CreateCommand();
        checkCol.CommandText = $"PRAGMA table_info({TableName})";
        var hasUserId = false;
        await using (var reader = await checkCol.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                if (reader.GetString(1) == "user_id") hasUserId = true;
        }
        if (!hasUserId) return;

        await using var m = connection.CreateCommand();
        m.CommandText =
            $"CREATE TABLE {TableName}_new (config_key TEXT NOT NULL PRIMARY KEY, config_json TEXT NOT NULL, cached_at TEXT NOT NULL);" +
            $"INSERT OR REPLACE INTO {TableName}_new (config_key, config_json, cached_at) " +
            $"SELECT config_key, config_json, MAX(cached_at) FROM {TableName} GROUP BY config_key;" +
            $"DROP TABLE {TableName};" +
            $"ALTER TABLE {TableName}_new RENAME TO {TableName};";
        await m.ExecuteNonQueryAsync(ct);
    }
}
