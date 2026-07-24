using System.Globalization;
using System.IO;
using Acme.Qifu.Domain.Ports;
using Microsoft.Data.Sqlite;

namespace Acme.Qifu.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed <see cref="IQifuConfigCache"/>. Persists qifu tokens / sids /
/// client configs to a local SQLite file so they survive process restarts —
/// matching AlphaAgent's EF Core SQLite token cache, but with a lightweight
/// single table and no EF dependency. The schema is auto-created on first use.
/// <para>
/// This is a qifu Infrastructure implementation (a persistence detail), NOT a
/// separate bounded context. The SQLite package is referenced only here; the
/// Domain/Application layers stay free of it.
/// </para>
/// </summary>
public sealed class SqliteQifuConfigCache : IQifuConfigCache
{
    private const string TableName = "qifu_config_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "user_id TEXT NOT NULL, " +
        "config_key TEXT NOT NULL, " +
        "config_json TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL, " +
        "PRIMARY KEY (user_id, config_key))";

    private readonly string _connectionString;
    private int _initialized;

    /// <param name="connectionString">A SQLite connection string, e.g. <c>Data Source=/path/to/qifu.db</c>.</param>
    public SqliteQifuConfigCache(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    /// <summary>Convenience factory: builds a <c>Data Source=&lt;dbPath&gt;</c> connection string.</summary>
    public static SqliteQifuConfigCache ForFile(string dbPath)
        => new($"Data Source={dbPath}");

    /// <inheritdoc/>
    public async Task<QifuConfigCacheItem?> GetAsync(string userId, string configKey, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT config_json, cached_at FROM {TableName} WHERE user_id = @u AND config_key = @k";
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@k", configKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new QifuConfigCacheItem
        {
            UserId = userId,
            ConfigKey = configKey,
            ConfigJson = reader.GetString(0),
            CachedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)
        };
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(QifuConfigCacheItem item, CancellationToken ct = default)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        // Always stamp the write time (matches MemoryQifuConfigCache behavior).
        item.CachedAt = DateTimeOffset.UtcNow;

        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} (user_id, config_key, config_json, cached_at) " +
            "VALUES (@u, @k, @j, @t)";
        cmd.Parameters.AddWithValue("@u", item.UserId);
        cmd.Parameters.AddWithValue("@k", item.ConfigKey);
        cmd.Parameters.AddWithValue("@j", item.ConfigJson);
        cmd.Parameters.AddWithValue("@t", item.CachedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string userId, string configKey, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE user_id = @u AND config_key = @k";
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@k", configKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Creates the table (and the parent directory) on first use. Safe under races — <c>IF NOT EXISTS</c> is idempotent.</summary>
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        try
        {
            // Ensure the parent directory exists for file-based databases.
            var dbPath = ExtractDataSourcePath();
            if (!string.IsNullOrEmpty(dbPath))
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            await using var conn = await OpenConnectionAsync(ct);
            // WAL is a database-level persistent setting — set once so concurrent
            // readers/writers (e.g. other bounded contexts sharing this file) don't
            // block each other. Safe to run repeatedly.
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
            // Allow a retry on the next call if init failed.
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>Opens a connection and sets a per-connection busy_timeout so concurrent
    /// writers (other contexts/processes sharing the file) wait briefly for the lock
    /// instead of failing immediately with <c>database is locked</c>.</summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    /// <summary>Best-effort extraction of the file path from a <c>Data Source=...</c> connection string (for directory creation).</summary>
    private string? ExtractDataSourcePath()
    {
        var parts = _connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                var val = part[(eq + 1)..].Trim();
                // Skip special/in-memory sources — no directory to create.
                if (val.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || val == "|:memory:|")
                    return null;
                return val;
            }
        }
        return null;
    }
}
