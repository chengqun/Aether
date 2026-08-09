using System.Globalization;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Mobile.Shared.Services.Caching;

/// <summary>
/// SQLite-backed <see cref="IMessageCacheService"/>（离线优先消息缓存）。一消息一行（不照搬
/// AlphaAgent 的单会话 JSON blob + 字符串拼接）。表 <c>message_cache</c>，共享 social-cache.db。
/// 遵循 <see cref="SqliteStoreBase"/> 约定：lazy <c>EnsureSchemaAsync</c>（Interlocked 守卫）+ 全 async ConfigureAwait(false)。
/// </summary>
public sealed class SqliteMessageCacheStore : SqliteStoreBase, IMessageCacheService
{
    private const string TableName = "message_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "conversation_id TEXT NOT NULL, " +
        "sender_id TEXT NOT NULL, " +
        "sender_name TEXT, " +
        "content TEXT NOT NULL, " +
        "message_type TEXT NOT NULL, " +
        "creation_time TEXT NOT NULL, " +
        "is_read INTEGER NOT NULL DEFAULT 0);" +
        "CREATE INDEX IF NOT EXISTS idx_message_conv ON " + TableName + " (conversation_id, creation_time);";

    private const string Columns =
        "id, conversation_id, sender_id, sender_name, content, message_type, creation_time, is_read";

    public SqliteMessageCacheStore(string connectionString) : base(connectionString) { }

    public static SqliteMessageCacheStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<List<ChatMessage>> GetCachedMessagesAsync(Guid conversationId, int? limit = null)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = limit is > 0
            ? $"SELECT {Columns} FROM {TableName} WHERE conversation_id=@c ORDER BY creation_time LIMIT @l"
            : $"SELECT {Columns} FROM {TableName} WHERE conversation_id=@c ORDER BY creation_time";
        cmd.Parameters.AddWithValue("@c", conversationId.ToString());
        if (limit is > 0) cmd.Parameters.AddWithValue("@l", limit.Value);

        var list = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            list.Add(ReadMessage(reader));
        return list;
    }

    public async Task<DateTimeOffset?> GetLastCachedAtAsync(Guid conversationId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX(creation_time) FROM {TableName} WHERE conversation_id=@c";
        cmd.Parameters.AddWithValue("@c", conversationId.ToString());
        var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        if (raw is null or DBNull) return null;
        return DateTime.Parse((string)raw, CultureInfo.InvariantCulture);
    }

    public async Task CacheMessagesAsync(Guid conversationId, List<ChatMessage> messages, int? maxCacheSize = null)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        // 全量替换该会话的缓存
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {TableName} WHERE conversation_id=@c";
            del.Parameters.AddWithValue("@c", conversationId.ToString());
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        foreach (var m in messages)
            await InsertAsync(conn, tx, m).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);

        // 超限裁剪：保留最新 N 条
        if (maxCacheSize is > 0)
        {
            await using var trim = conn.CreateCommand();
            trim.CommandText =
                $"DELETE FROM {TableName} WHERE conversation_id=@c AND id NOT IN " +
                $"(SELECT id FROM {TableName} WHERE conversation_id=@c ORDER BY creation_time DESC LIMIT @n)";
            trim.Parameters.AddWithValue("@c", conversationId.ToString());
            trim.Parameters.AddWithValue("@n", maxCacheSize.Value);
            await trim.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    public async Task AppendMessageAsync(Guid conversationId, ChatMessage message)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        await InsertAsync(conn, tx, message).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task ClearCacheAsync(Guid conversationId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE conversation_id=@c";
        cmd.Parameters.AddWithValue("@c", conversationId.ToString());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertAsync(SqliteConnection conn, SqliteTransaction tx, ChatMessage m)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} " +
            "(id, conversation_id, sender_id, sender_name, content, message_type, creation_time, is_read) " +
            "VALUES (@id, @c, @s, @sn, @ct, @mt, @cr, @r)";
        cmd.Parameters.AddWithValue("@id", m.Id.ToString());
        cmd.Parameters.AddWithValue("@c", m.ConversationId.ToString());
        cmd.Parameters.AddWithValue("@s", m.SenderId.ToString());
        cmd.Parameters.AddWithValue("@sn", (object?)m.SenderName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ct", m.Content);
        cmd.Parameters.AddWithValue("@mt", m.MessageType);
        cmd.Parameters.AddWithValue("@cr", m.CreationTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@r", m.IsRead ? 1 : 0);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static ChatMessage ReadMessage(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        ConversationId = Guid.Parse(r.GetString(1)),
        SenderId = Guid.Parse(r.GetString(2)),
        SenderName = r.IsDBNull(3) ? null : r.GetString(3),
        Content = r.GetString(4),
        MessageType = r.GetString(5),
        CreationTime = DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture),
        IsRead = r.GetInt64(7) != 0,
    };
}
