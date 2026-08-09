using System.Globalization;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Mobile.Shared.Services.Caching;

/// <summary>
/// SQLite-backed <see cref="IContactSyncService"/>（通讯录缓存 + 服务端同步）。表 <c>contact_cache</c>，
/// 共享 social-cache.db。同步：拉 <see cref="IRelationshipService.GetAcceptedContactsAsync"/> → 全量替换（DELETE+INSERT）。
/// 返回扁平 <see cref="Relationship"/> 列表（VM 按 Type 分类）。网络失败回退缓存。
/// </summary>
public sealed class SqliteContactCacheStore : SqliteStoreBase, IContactSyncService
{
    private const string TableName = "contact_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL, " +
        "type INTEGER NOT NULL, " +
        "target_id TEXT NOT NULL, " +
        "target_name TEXT NOT NULL, " +
        "device_type TEXT, " +
        "status INTEGER NOT NULL, " +
        "user_id TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL, " +
        "PRIMARY KEY (id, user_id));" +
        "CREATE INDEX IF NOT EXISTS idx_contact_user ON " + TableName + " (user_id);";

    private const string Columns = "id, type, target_id, target_name, device_type, status";

    private readonly IRelationshipService? _relationshipService;

    public SqliteContactCacheStore(string connectionString, IRelationshipService? relationshipService = null)
        : base(connectionString) => _relationshipService = relationshipService;

    public async Task<List<Relationship>> GetCachedContactsAsync(Guid userId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE user_id=@u";
        cmd.Parameters.AddWithValue("@u", userId.ToString());

        var list = new List<Relationship>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            list.Add(ReadRelationship(reader));
        return list;
    }

    public async Task<List<Relationship>> SyncFromServerAsync(Guid userId)
    {
        if (_relationshipService == null)
            return await GetCachedContactsAsync(userId).ConfigureAwait(false);

        List<Relationship> server;
        try
        {
            server = await _relationshipService.GetAcceptedContactsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ContactSync] 拉取服务端通讯录失败，回退缓存: {ex.Message}");
            return await GetCachedContactsAsync(userId).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

        // 全量替换：先删该用户全部，再批量插
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {TableName} WHERE user_id=@u";
            del.Parameters.AddWithValue("@u", userId.ToString());
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        foreach (var r in server)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {TableName} " +
                "(id, type, target_id, target_name, device_type, status, user_id, cached_at) " +
                "VALUES (@id,@t,@tid,@tn,@dt,@s,@u,@ca)";
            cmd.Parameters.AddWithValue("@id", r.Id.ToString());
            cmd.Parameters.AddWithValue("@t", r.Type);
            cmd.Parameters.AddWithValue("@tid", r.TargetId);
            cmd.Parameters.AddWithValue("@tn", r.TargetName);
            cmd.Parameters.AddWithValue("@dt", (object?)r.DeviceType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", r.Status);
            cmd.Parameters.AddWithValue("@u", userId.ToString());
            cmd.Parameters.AddWithValue("@ca", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await tx.CommitAsync().ConfigureAwait(false);
        return server;
    }

    private static Relationship ReadRelationship(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Type = (int)r.GetInt64(1),
        TargetId = r.GetString(2),
        TargetName = r.GetString(3),
        DeviceType = r.IsDBNull(4) ? null : r.GetString(4),
        Status = (int)r.GetInt64(5),
    };
}
