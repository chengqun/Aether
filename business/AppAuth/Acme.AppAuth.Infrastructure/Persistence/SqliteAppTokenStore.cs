using System.Globalization;
using Acme.AppAuth.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.AppAuth.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IAppTokenStore"/>. Caches the current app-server
/// session token (access + refresh + username + expiry) in a single-row table
/// <c>platform__session</c> so the login survives process restarts. WAL +
/// busy_timeout are set so the shared SQLite file plays nice with other bounded
/// contexts writing concurrently.
/// </summary>
public sealed class SqliteAppTokenStore : SqliteStoreBase, IAppTokenStore
{
    private const string TableName = "platform__session";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY CHECK (id = 1), " +
        "access_token TEXT NOT NULL, " +
        "refresh_token TEXT NOT NULL, " +
        "username TEXT NOT NULL, " +
        "expires_at TEXT NOT NULL)";

    public SqliteAppTokenStore(string connectionString) : base(connectionString) { }

    public static SqliteAppTokenStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

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
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
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
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = 1";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> GetColumnAsync(string column, CancellationToken ct)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM {TableName} WHERE id = 1";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }
}
