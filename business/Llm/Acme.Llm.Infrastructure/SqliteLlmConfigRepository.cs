using System.Globalization;
using Acme.Llm.Domain;
using Acme.Persistence;
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
/// 基类 <see cref="SqliteStoreBase"/> 的 async 方法同样 ConfigureAwait(false)，故整条链安全。</para>
/// </summary>
public sealed class SqliteLlmConfigRepository : SqliteStoreBase, ILlmConfigRepository
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

    public SqliteLlmConfigRepository(string connectionString) : base(connectionString) { }

    public static SqliteLlmConfigRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    /// <inheritdoc/>
    public async Task<List<LlmConfig>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName}";
        return await ReadAllAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpsertRangeAsync(IEnumerable<LlmConfig> items, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
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
        await EnsureSchemaAsync(CreateSql, ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
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
}
