using System.Globalization;
using System.IO;
using Acme.AppAuth.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.AppAuth.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IAppTokenStore"/>. Caches the current app-server
/// session token (access + refresh + username + expiry) in a single-row table
/// <c>platform__session</c> so the login survives process restarts. WAL +
/// busy_timeout are set so the shared SQLite file plays nice with other bounded
/// contexts writing concurrently.
/// </summary>
public sealed class SqliteAppTokenStore : IAppTokenStore
{
    private const string TableName = "platform__session";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY CHECK (id = 1), " +
        "access_token TEXT NOT NULL, " +
        "refresh_token TEXT NOT NULL, " +
        "username TEXT NOT NULL, " +
        "expires_at TEXT NOT NULL)";

    private readonly string _connectionString;
    private int _initialized;

    public SqliteAppTokenStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public static SqliteAppTokenStore ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => await GetColumnAsync("access_token", ct);

    public async Task<string?> GetRefreshTokenAsync(CancellationToken ct = default)
        => await GetColumnAsync("refresh_token", ct);

    public async Task<string?> GetUsernameAsync(CancellationToken ct = default)
        => await GetColumnAsync("username", ct);

    public async Task<DateTimeOffset?> GetExpiresAtAsync(CancellationToken ct = default)
    {
        var raw = await GetColumnAsync("expires_at", ct);
        return raw is null ? null : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture);
    }

    public async Task SaveAsync(AppToken token, CancellationToken ct = default)
    {
        if (token is null) throw new ArgumentNullException(nameof(token));
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} (id, access_token, refresh_token, username, expires_at) " +
            "VALUES (1, @a, @r, @u, @e)";
        cmd.Parameters.AddWithValue("@a", token.AccessToken);
        cmd.Parameters.AddWithValue("@r", token.RefreshToken);
        cmd.Parameters.AddWithValue("@u", token.Username);
        cmd.Parameters.AddWithValue("@e", token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = 1";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> GetColumnAsync(string column, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {TableName} WHERE id = 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
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
