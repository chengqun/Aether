using System.Globalization;
using Acme.Persistence;
using Acme.Qifu.Domain.Ports;
using Microsoft.Data.Sqlite;

namespace Acme.Qifu.Mobile.Persistence;

/// <summary>
/// 手机端 <see cref="IQifuConfigCache"/> 实现（SQLite，单用户 key-value）。
/// 镜像桌面 <c>Acme.Qifu.Infrastructure.Persistence.SqliteQifuConfigCache</c>，但不引 Infrastructure
/// （含 <c>Microsoft.Playwright</c>，net10.0-ios 不支持）。手机端 DB 全新，无需桌面端的 user_id 旧表迁移。
/// 遵循 <see cref="SqliteStoreBase"/> 约定：lazy EnsureSchemaAsync + ForFile 工厂 + 全 async。
/// </summary>
internal sealed class MobileQifuConfigCache : SqliteStoreBase, IQifuConfigCache
{
    private const string TableName = "qifu_config_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "config_key TEXT NOT NULL PRIMARY KEY, " +
        "config_json TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL)";

    private MobileQifuConfigCache(string connectionString) : base(connectionString) { }

    public static MobileQifuConfigCache ForFile(string dbPath)
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
}
