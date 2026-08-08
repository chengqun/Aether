using Acme.Ctp.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Ctp.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ICtpConfigStore"/>. Single-row table <c>ctp_config</c>
/// (id=1) holds the saved CTP connection credentials. WAL + busy_timeout set.
/// </summary>
public sealed class SqliteCtpConfigStore : SqliteStoreBase, ICtpConfigStore
{
    private const string TableName = "ctp_config";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY CHECK (id = 1), " +
        "front_address TEXT NOT NULL, md_front_address TEXT NOT NULL, " +
        "broker_id TEXT NOT NULL, user_id TEXT NOT NULL, password TEXT NOT NULL, " +
        "app_id TEXT NOT NULL, auth_code TEXT NOT NULL, auto_login INTEGER NOT NULL DEFAULT 0)";

    public SqliteCtpConfigStore(string connectionString) : base(connectionString) { }

    public static SqliteCtpConfigStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
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
        await EnsureSchemaAsync(CreateSql, ct);
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
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = 1";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>迁移：旧表无 auto_login 列时添加（幂等）。</summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
        => await SqliteMigrations.AddColumnAsync(connection, TableName, "auto_login", "INTEGER NOT NULL DEFAULT 0", ct);
}
