using System.Globalization;
using System.IO;
using Acme.Ctp.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Ctp.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ICtpConfigStore"/>. Single-row table <c>ctp_config</c>
/// (id=1) holds the saved CTP connection credentials. WAL + busy_timeout set.
/// </summary>
public sealed class SqliteCtpConfigStore : ICtpConfigStore
{
    private const string TableName = "ctp_config";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY CHECK (id = 1), " +
        "front_address TEXT NOT NULL, md_front_address TEXT NOT NULL, " +
        "broker_id TEXT NOT NULL, user_id TEXT NOT NULL, password TEXT NOT NULL, " +
        "app_id TEXT NOT NULL, auth_code TEXT NOT NULL, auto_login INTEGER NOT NULL DEFAULT 0)";

    private const string MigrateAutoLoginSql =
        "ALTER TABLE " + TableName + " ADD COLUMN auto_login INTEGER NOT NULL DEFAULT 0";

    private readonly string _connectionString;
    private int _initialized;

    public SqliteCtpConfigStore(string connectionString)
        => _connectionString = !string.IsNullOrWhiteSpace(connectionString) ? connectionString : throw new ArgumentException("required", nameof(connectionString));

    public static SqliteCtpConfigStore ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT front_address, md_front_address, broker_id, user_id, password, app_id, auth_code, auto_login FROM {TableName} WHERE id = 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new CtpConnectionConfig
        {
            FrontAddress = reader.GetString(0),
            MdFrontAddress = reader.GetString(1),
            BrokerId = reader.GetString(2),
            UserId = reader.GetString(3),
            Password = reader.GetString(4),
            AppId = reader.GetString(5),
            AuthCode = reader.GetString(6),
            AutoLogin = reader.GetInt32(7) != 0
        };
    }

    public async Task SaveAsync(CtpConnectionConfig config, CancellationToken ct = default)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {TableName} (id, front_address, md_front_address, broker_id, user_id, password, app_id, auth_code, auto_login) " +
            "VALUES (1, @f, @m, @b, @u, @p, @a, @c, @al) " +
            "ON CONFLICT(id) DO UPDATE SET front_address=excluded.front_address, md_front_address=excluded.md_front_address, " +
            "broker_id=excluded.broker_id, user_id=excluded.user_id, password=excluded.password, app_id=excluded.app_id, auth_code=excluded.auth_code, auto_login=excluded.auto_login";
        cmd.Parameters.AddWithValue("@f", config.FrontAddress);
        cmd.Parameters.AddWithValue("@m", config.MdFrontAddress);
        cmd.Parameters.AddWithValue("@b", config.BrokerId);
        cmd.Parameters.AddWithValue("@u", config.UserId);
        cmd.Parameters.AddWithValue("@p", config.Password);
        cmd.Parameters.AddWithValue("@a", config.AppId);
        cmd.Parameters.AddWithValue("@c", config.AuthCode);
        cmd.Parameters.AddWithValue("@al", config.AutoLogin ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = 1";
        await cmd.ExecuteNonQueryAsync(ct);
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
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            await using var conn = await OpenAsync(ct);
            await using (var wal = conn.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; await wal.ExecuteNonQueryAsync(ct); }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync(ct);
            // 迁移：旧表无 auto_login 列时添加（已存在则忽略）
            try
            {
                await using var mig = conn.CreateCommand();
                mig.CommandText = MigrateAutoLoginSql;
                await mig.ExecuteNonQueryAsync(ct);
            }
            catch { /* 列已存在，忽略 */ }
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

    private string? ExtractDataSourcePath()
    {
        foreach (var part in _connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                var val = part[(eq + 1)..].Trim();
                return val.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ? null : val;
            }
        }
        return null;
    }
}
