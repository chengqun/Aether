using System.IO;
using Microsoft.Data.Sqlite;

namespace Acme.Persistence;

/// <summary>
/// Shared base for SQLite-backed stores. Collapses the copy-pasted boilerplate
/// (connection-string validation, <c>ForFile</c> factory, <c>busy_timeout</c> +
/// WAL PRAGMAs, idempotent lazy schema init, data-source path extraction) that
/// was duplicated across every business Infrastructure repo and the platform
/// <c>.Sqlite</c> stores.
/// <para>
/// <b>Swap-aware, not swap-ready.</b> Connection-string construction + PRAGMA
/// setup live here, so a future runtime swap rewrites this one class rather than
/// 15 scattered files. Per-store raw SQL (CREATE TABLE / INSERT OR REPLACE) is
/// still SQLite dialect and would need per-store rewriting on a real swap.
/// </para>
/// </summary>
public abstract class SqliteStoreBase
{
    protected readonly string ConnectionString;
    private int _initialized;

    protected SqliteStoreBase(string connectionString)
        => ConnectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string is required.", nameof(connectionString));

    /// <summary>Builds a <c>Data Source=</c> connection string for a file path (the swap seam).</summary>
    public static string BuildConnectionString(string dbPath) => $"Data Source={dbPath}";

    /// <summary>When true, <see cref="OpenAsync"/>/<see cref="Open"/> set <c>PRAGMA foreign_keys=ON</c>
    /// on every connection. Default false; stores with FK-cascade tables (e.g. conversation→message) override to true.</summary>
    protected virtual bool ForeignKeysEnabled => false;

    /// <summary>Opens a connection with <c>busy_timeout=5000</c> (and foreign_keys when enabled).
    /// Uses <c>ConfigureAwait(false)</c> — library best practice; some stores (e.g. Llm) are consumed
    /// sync-over-async on the UI thread and would deadlock without it.</summary>
    protected async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqliteConnection(ConnectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var p = c.CreateCommand();
        p.CommandText = ForeignKeysEnabled
            ? "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"
            : "PRAGMA busy_timeout=5000;";
        await p.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return c;
    }

    /// <summary>Synchronous open — for ctor-time init only (see <see cref="EnsureSchema"/>).</summary>
    protected SqliteConnection Open()
    {
        var c = new SqliteConnection(ConnectionString);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = ForeignKeysEnabled
            ? "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;"
            : "PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    /// <summary>Idempotent lazy schema init: creates the parent directory, enables WAL, runs
    /// <paramref name="createSql"/>, then <see cref="MigrateAsync"/>. Runs once; resets on failure so the next call retries.</summary>
    protected async Task EnsureSchemaAsync(string createSql, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            EnsureDirectory();
            await using var c = await OpenAsync(ct).ConfigureAwait(false);
            await using (var wal = c.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var cmd = c.CreateCommand();
            cmd.CommandText = createSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await MigrateAsync(c, ct).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>Synchronous idempotent schema init for stores that must init in their ctor
    /// (e.g. Automation — avoids async-over-sync deadlock on the UI thread; see #3a).</summary>
    protected void EnsureSchema(string createSql)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            EnsureDirectory();
            using var c = Open();
            using (var wal = c.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            using var cmd = c.CreateCommand();
            cmd.CommandText = createSql;
            cmd.ExecuteNonQuery();
            Migrate(c);
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
    }

    /// <summary>Override to run idempotent column migrations after schema creation (async path).</summary>
    protected virtual Task MigrateAsync(SqliteConnection connection, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Override to run idempotent column migrations after schema creation (sync path).</summary>
    protected virtual void Migrate(SqliteConnection connection) { }

    private void EnsureDirectory()
    {
        var dbPath = ExtractDataSourcePath(ConnectionString);
        if (string.IsNullOrEmpty(dbPath)) return;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Extracts the file path from a <c>Data Source=</c>/<c>Filename=</c> segment;
    /// returns null for <c>:memory:</c> or in-memory/uri connection strings.</summary>
    protected static string? ExtractDataSourcePath(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
