using System.Globalization;
using Acme.Mobile.Mvvm.Services;
using Acme.Moments.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Mobile.Shared.Services.Caching;

/// <summary>
/// SQLite-backed <see cref="IMomentCacheService"/>（朋友圈离线缓存）。表 <c>moment_cache</c>，共享 social-cache.db。
/// 纯缓存（无 SyncFromServer——VM 调 <c>IMomentService</c> 拉取后 <c>UpdateCacheAsync</c>）。增量同步靠
/// <see cref="GetLatestCachedCreatedAtAsync"/> 提供的 since 游标。
/// </summary>
public sealed class SqliteMomentCacheStore : SqliteStoreBase, IMomentCacheService
{
    private const string TableName = "moment_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "user_id TEXT NOT NULL, " +
        "username TEXT NOT NULL, " +
        "content TEXT NOT NULL, " +
        "image_url TEXT, " +
        "created_at TEXT NOT NULL, " +
        "type TEXT NOT NULL, " +
        "visibility TEXT NOT NULL, " +
        "target_id TEXT);" +
        "CREATE INDEX IF NOT EXISTS idx_moment_created ON " + TableName + " (created_at DESC);" +
        "CREATE INDEX IF NOT EXISTS idx_moment_target ON " + TableName + " (target_id);";

    private const string Columns =
        "id, user_id, username, content, image_url, created_at, type, visibility, target_id";

    public SqliteMomentCacheStore(string connectionString) : base(connectionString) { }

    public static SqliteMomentCacheStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<List<Moment>> GetCachedMomentsAsync()
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} ORDER BY created_at DESC";
        return await ReadAllAsync(cmd).ConfigureAwait(false);
    }

    public async Task<List<Moment>> GetCachedMomentsAsync(string targetId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE target_id=@t ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@t", targetId);
        return await ReadAllAsync(cmd).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetLatestCachedCreatedAtAsync()
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX(created_at) FROM {TableName}";
        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw is null or DBNull) return null;
        return DateTime.Parse((string)raw, CultureInfo.InvariantCulture);
    }

    public async Task UpdateCacheAsync(List<Moment> moments)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var m in moments)
            await UpsertAsync(conn, tx, m).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task AddMomentAsync(Moment moment)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        await UpsertAsync(conn, tx, moment).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task ClearCacheAsync()
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName}";
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task UpsertAsync(SqliteConnection conn, SqliteTransaction tx, Moment m)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} " +
            "(id, user_id, username, content, image_url, created_at, type, visibility, target_id) " +
            "VALUES (@id,@u,@un,@c,@i,@ca,@t,@v,@tid)";
        cmd.Parameters.AddWithValue("@id", m.Id.ToString());
        cmd.Parameters.AddWithValue("@u", m.UserId.ToString());
        cmd.Parameters.AddWithValue("@un", m.Username);
        cmd.Parameters.AddWithValue("@c", m.Content);
        cmd.Parameters.AddWithValue("@i", (object?)m.ImageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", m.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@t", m.Type);
        cmd.Parameters.AddWithValue("@v", m.Visibility);
        cmd.Parameters.AddWithValue("@tid", (object?)m.TargetId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<List<Moment>> ReadAllAsync(SqliteCommand cmd)
    {
        var list = new List<Moment>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            list.Add(new Moment
            {
                Id = Guid.Parse(reader.GetString(0)),
                UserId = Guid.Parse(reader.GetString(1)),
                Username = reader.GetString(2),
                Content = reader.GetString(3),
                ImageUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                Type = reader.GetString(6),
                Visibility = reader.GetString(7),
                TargetId = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
        return list;
    }
}
