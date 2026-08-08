using System.Globalization;
using System.IO;
using Acme.Llm.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Llm.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ILlmConfigRepository"/>. Table <c>llm_config</c>
/// holds the local device's LLM provider configs. No per-user column — the
/// local device is single-user (the logged-in app user). WAL + busy_timeout are
/// set so the shared SQLite file cooperates with other bounded contexts.
/// <para><b>ConfigureAwait(false)</b>：此 repo 被 <c>DesktopLlmBootstrap</c> 在 UI 线程上
/// sync-over-async 消费（<c>AddDesktopCommon</c> → <c>GetOrCreateActive</c> → <c>.GetAwaiter().GetResult()</c>）。
/// 库代码加 ConfigureAwait(false) 让 continuation 跑线程池、不回 UI SynchronizationContext → 不死锁。
/// 对正常 async 调用路径无副作用（库代码最佳实践）。</para>
/// </summary>
public sealed class SqliteLlmConfigRepository : ILlmConfigRepository
{
    private const string TableName = "llm_config";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "name TEXT NOT NULL, " +
        "model_name TEXT NOT NULL, " +
        "api_key TEXT NOT NULL, " +
        "endpoint TEXT NOT NULL, " +
        "temperature REAL NOT NULL, " +
        "is_default INTEGER NOT NULL, " +
        "is_system INTEGER NOT NULL, " +
        "cached_at TEXT NOT NULL)";

    private static readonly string SelectColumns =
        "id, name, model_name, api_key, endpoint, temperature, is_default, is_system, cached_at";

    private readonly string _connectionString;
    private int _initialized;

    public SqliteLlmConfigRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQLite connection string is required.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public static SqliteLlmConfigRepository ForFile(string dbPath) => new($"Data Source={dbPath}");

    /// <inheritdoc/>
    public async Task<List<LlmConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName}";
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpsertRangeAsync(IEnumerable<LlmConfig> items, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT OR REPLACE INTO {TableName} " +
                "(id, name, model_name, api_key, endpoint, temperature, is_default, is_system, cached_at) " +
                "VALUES (@id, @n, @m, @k, @e, @t, @d, @s, @c)";
            cmd.Parameters.AddWithValue("@id", item.Id.ToString());
            cmd.Parameters.AddWithValue("@n", item.Name);
            cmd.Parameters.AddWithValue("@m", item.ModelName);
            cmd.Parameters.AddWithValue("@k", item.ApiKey);
            cmd.Parameters.AddWithValue("@e", item.Endpoint);
            cmd.Parameters.AddWithValue("@t", item.Temperature);
            cmd.Parameters.AddWithValue("@d", item.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("@s", item.IsSystem ? 1 : 0);
            cmd.Parameters.AddWithValue("@c", item.CachedAt.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName}";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<LlmConfig>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<LlmConfig>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new LlmConfig
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                ModelName = reader.GetString(2),
                ApiKey = reader.GetString(3),
                Endpoint = reader.GetString(4),
                Temperature = (float)reader.GetDouble(5),
                IsDefault = reader.GetInt64(6) != 0,
                IsSystem = reader.GetInt64(7) != 0,
                CachedAt = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)
            });
        }
        return list;
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
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
