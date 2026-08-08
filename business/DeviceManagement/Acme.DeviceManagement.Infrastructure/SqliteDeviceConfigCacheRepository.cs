using System.Globalization;
using Acme.DeviceManagement.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.DeviceManagement.Infrastructure;

public sealed class SqliteDeviceConfigCacheRepository : SqliteStoreBase, IDeviceConfigCacheRepository
{
    private const string TableName = "device_config";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "user_id TEXT NOT NULL, " +
        "server_url TEXT NOT NULL, " +
        "authorization_code TEXT NOT NULL, " +
        "device_name TEXT NOT NULL, " +
        "device_type TEXT NOT NULL, " +
        "last_agent_name TEXT NOT NULL, " +
        "recent_agent_names_json TEXT, " +
        "last_llm_id TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL)";

    private static readonly string SelectColumns =
        "id, user_id, server_url, authorization_code, device_name, device_type, last_agent_name, recent_agent_names_json, last_llm_id, cached_at";

    public SqliteDeviceConfigCacheRepository(string connectionString) : base(connectionString) { }

    public static SqliteDeviceConfigCacheRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<DeviceConfigCacheItem?> GetByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId.ToString());
        return await ReadOneAsync(cmd, ct);
    }

    public async Task<DeviceConfigCacheItem?> GetAnyAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} LIMIT 1";
        return await ReadOneAsync(cmd, ct);
    }

    public async Task UpsertAsync(DeviceConfigCacheItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} " +
            "(id, user_id, server_url, authorization_code, device_name, device_type, last_agent_name, recent_agent_names_json, last_llm_id, cached_at) " +
            "VALUES (@id, @uid, @su, @ac, @dn, @dt, @lan, @ranj, @llm, @ca)";
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@uid", item.UserId.ToString());
        cmd.Parameters.AddWithValue("@su", item.ServerUrl);
        cmd.Parameters.AddWithValue("@ac", item.AuthorizationCode);
        cmd.Parameters.AddWithValue("@dn", item.DeviceName);
        cmd.Parameters.AddWithValue("@dt", item.DeviceType);
        cmd.Parameters.AddWithValue("@lan", item.LastAgentName);
        cmd.Parameters.AddWithValue("@ranj", item.RecentAgentNamesJson ?? string.Empty);
        cmd.Parameters.AddWithValue("@llm", item.LastLlmId.ToString());
        cmd.Parameters.AddWithValue("@ca", item.CachedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<DeviceConfigCacheItem?> ReadOneAsync(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new DeviceConfigCacheItem
        {
            Id = Guid.Parse(reader.GetString(0)),
            UserId = Guid.Parse(reader.GetString(1)),
            ServerUrl = reader.GetString(2),
            AuthorizationCode = reader.GetString(3),
            DeviceName = reader.GetString(4),
            DeviceType = reader.GetString(5),
            LastAgentName = reader.GetString(6),
            RecentAgentNamesJson = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastLlmId = Guid.Parse(reader.GetString(8)),
            CachedAt = DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture)
        };
    }
}
