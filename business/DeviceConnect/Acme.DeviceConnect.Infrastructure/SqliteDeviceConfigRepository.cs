using System.Globalization;
using Acme.DeviceConnect.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.DeviceConnect.Infrastructure;

public sealed class SqliteDeviceConfigRepository : SqliteStoreBase
{
    private const string TableName = "device_config";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "server_url TEXT NOT NULL, " +
        "authorization_code TEXT NOT NULL, " +
        "device_name TEXT NOT NULL, " +
        "device_type TEXT NOT NULL, " +
        "tenant TEXT NOT NULL, " +
        "auto_connect INTEGER NOT NULL, " +
        "cached_at TEXT NOT NULL)";

    private static readonly string SelectColumns =
        "id, server_url, authorization_code, device_name, device_type, tenant, auto_connect, cached_at";

    public SqliteDeviceConfigRepository(string connectionString) : base(connectionString) { }

    public static SqliteDeviceConfigRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<DeviceConfigCacheItem?> GetByTenantAsync(string tenant, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE tenant = @tenant";
        cmd.Parameters.AddWithValue("@tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadDeviceConfig(reader);
    }

    public async Task UpsertAsync(DeviceConfigCacheItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} " +
            "(id, server_url, authorization_code, device_name, device_type, tenant, auto_connect, cached_at) " +
            "VALUES (@id, @su, @ac, @dn, @dt, @t, @acn, @c)";
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@su", item.ServerUrl);
        cmd.Parameters.AddWithValue("@ac", item.AuthorizationCode);
        cmd.Parameters.AddWithValue("@dn", item.DeviceName);
        cmd.Parameters.AddWithValue("@dt", item.DeviceType);
        cmd.Parameters.AddWithValue("@t", item.Tenant);
        cmd.Parameters.AddWithValue("@acn", item.AutoConnect ? 1 : 0);
        cmd.Parameters.AddWithValue("@c", item.CachedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DeviceConfigCacheItem ReadDeviceConfig(SqliteDataReader reader)
    {
        return new DeviceConfigCacheItem
        {
            Id = Guid.Parse(reader.GetString(0)),
            ServerUrl = reader.GetString(1),
            AuthorizationCode = reader.GetString(2),
            DeviceName = reader.GetString(3),
            DeviceType = reader.GetString(4),
            Tenant = reader.GetString(5),
            AutoConnect = reader.GetInt64(6) != 0,
            CachedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)
        };
    }
}
