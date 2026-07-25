using System.Globalization;
using System.IO;
using Acme.DeviceConnect.Domain;
using Microsoft.Data.Sqlite;
using System.Threading;

namespace Acme.DeviceConnect.Infrastructure;

public sealed class SqliteDeviceConfigRepository
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

    private readonly string _connectionString;
    private int _initialized;

    public SqliteDeviceConfigRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public static SqliteDeviceConfigRepository ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<DeviceConfigCacheItem?> GetByTenantAsync(string tenant, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE tenant = @tenant";
        cmd.Parameters.AddWithValue("@tenant", tenant);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadDeviceConfig(reader);
    }

    public async Task UpsertAsync(DeviceConfigCacheItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
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

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            var dbPath = ExtractDataSourcePath();
            if (!string.IsNullOrEmpty(dbPath))
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            await using var conn = await OpenConnectionAsync(ct);
            await using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private string? ExtractDataSourcePath()
    {
        foreach (var part in _connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                var val = part[(eq + 1)..].Trim();
                if (val.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return null;
                return val;
            }
        }
        return null;
    }
}