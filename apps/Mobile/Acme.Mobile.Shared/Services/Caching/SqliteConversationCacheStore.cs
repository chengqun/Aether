using System.Globalization;
using Acme.ChatSocial.Domain;
using Acme.Mobile.Mvvm.Services;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Mobile.Shared.Services.Caching;

/// <summary>
/// SQLite-backed <see cref="IConversationSyncService"/>（会话列表缓存 + 服务端同步）。表
/// <c>conversation_cache</c>，共享 social-cache.db。同步：拉 <see cref="IChatService.GetMyConversationsAsync"/>
/// → upsert → 删服务端已删（保留 Type 3/4 本地 agent 会话）。网络失败回退缓存。
/// </summary>
public sealed class SqliteConversationCacheStore : SqliteStoreBase, IConversationSyncService
{
    private const string TableName = "conversation_cache";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "type INTEGER NOT NULL, " +
        "name TEXT, " +
        "other_user_name TEXT, " +
        "other_user_id TEXT, " +
        "other_device_id TEXT, " +
        "device_type TEXT, " +
        "unread_count INTEGER NOT NULL DEFAULT 0, " +
        "last_message TEXT, " +
        "last_message_time TEXT, " +
        "member_count INTEGER NOT NULL DEFAULT 0, " +
        "context TEXT, " +
        "agent_name TEXT, " +
        "kind INTEGER, " +
        "user_id TEXT NOT NULL, " +
        "cached_at TEXT NOT NULL);" +
        "CREATE INDEX IF NOT EXISTS idx_conv_user ON " + TableName + " (user_id, last_message_time);";

    private const string Columns =
        "id, type, name, other_user_name, other_user_id, other_device_id, device_type, " +
        "unread_count, last_message, last_message_time, member_count, context, agent_name, kind";

    private readonly IChatService? _chatService;

    public SqliteConversationCacheStore(string connectionString, IChatService? chatService = null)
        : base(connectionString) => _chatService = chatService;

    public async Task<List<Conversation>> GetCachedConversationsAsync(Guid userId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE user_id=@u ORDER BY last_message_time DESC";
        cmd.Parameters.AddWithValue("@u", userId.ToString());

        var list = new List<Conversation>();
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            list.Add(ReadConversation(reader));
        return list;
    }

    public async Task<List<Conversation>> SyncFromServerAsync(Guid userId)
    {
        // 无后端服务或网络失败 → 回退缓存（离线优先）
        if (_chatService == null)
            return await GetCachedConversationsAsync(userId).ConfigureAwait(false);

        List<Conversation> server;
        try
        {
            server = await _chatService.GetMyConversationsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConvSync] 拉取服务端会话失败，回退缓存: {ex.Message}");
            return await GetCachedConversationsAsync(userId).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);

        var serverIds = new HashSet<string>();
        foreach (var c in server)
        {
            serverIds.Add(c.Id.ToString());
            await UpsertAsync(conn, tx, c, userId).ConfigureAwait(false);
        }

        // 删除该用户服务端已删的会话；保留 Type 3/4（agent/stock-agent 本地会话，服务端不返回）。
        // 取本地非 Type3/4 id → 与服务端集合差集 → 逐条删（避免动态 NOT IN 参数列表的脆弱性）。
        var staleIds = new List<string>();
        await using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = $"SELECT id FROM {TableName} WHERE user_id=@u AND type NOT IN (3,4)";
            q.Parameters.AddWithValue("@u", userId.ToString());
            await using var reader = await q.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                if (!serverIds.Contains(id)) staleIds.Add(id);
            }
        }
        foreach (var id in staleIds)
        {
            await using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {TableName} WHERE id=@i";
            del.Parameters.AddWithValue("@i", id);
            await del.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return await GetCachedConversationsAsync(userId).ConfigureAwait(false);
    }

    public async Task UpsertConversationAsync(Conversation conversation, Guid userId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
        await UpsertAsync(conn, tx, conversation, userId).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
    }

    public async Task DeleteConversationAsync(Guid conversationId)
    {
        await EnsureSchemaAsync(CreateSql).ConfigureAwait(false);
        await using var conn = await OpenAsync().ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id=@i";
        cmd.Parameters.AddWithValue("@i", conversationId.ToString());
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task UpsertAsync(SqliteConnection conn, SqliteTransaction tx, Conversation c, Guid userId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {TableName} " +
            "(id, type, name, other_user_name, other_user_id, other_device_id, device_type, " +
            "unread_count, last_message, last_message_time, member_count, context, agent_name, kind, user_id, cached_at) " +
            "VALUES (@id,@t,@n,@un,@ui,@ud,@dt,@uc,@lm,@lmt,@mc,@ctx,@an,@k,@u,@ca)";
        cmd.Parameters.AddWithValue("@id", c.Id.ToString());
        cmd.Parameters.AddWithValue("@t", c.Type);
        cmd.Parameters.AddWithValue("@n", (object?)c.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@un", (object?)c.OtherUserName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ui", c.OtherUserId.HasValue ? c.OtherUserId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@ud", (object?)c.OtherDeviceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dt", (object?)c.DeviceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uc", c.UnreadCount);
        cmd.Parameters.AddWithValue("@lm", (object?)c.LastMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lmt", c.LastMessageTime.HasValue ? c.LastMessageTime.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@mc", c.MemberCount);
        cmd.Parameters.AddWithValue("@ctx", (object?)c.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@an", (object?)c.AgentName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@k", c.Kind.HasValue ? c.Kind.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@u", userId.ToString());
        cmd.Parameters.AddWithValue("@ca", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static Conversation ReadConversation(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Type = (int)r.GetInt64(1),
        Name = r.IsDBNull(2) ? null : r.GetString(2),
        OtherUserName = r.IsDBNull(3) ? null : r.GetString(3),
        OtherUserId = r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4)),
        OtherDeviceId = r.IsDBNull(5) ? null : r.GetString(5),
        DeviceType = r.IsDBNull(6) ? null : r.GetString(6),
        UnreadCount = (int)r.GetInt64(7),
        LastMessage = r.IsDBNull(8) ? null : r.GetString(8),
        LastMessageTime = r.IsDBNull(9) ? null : DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture),
        MemberCount = (int)r.GetInt64(10),
        Context = r.IsDBNull(11) ? null : r.GetString(11),
        AgentName = r.IsDBNull(12) ? null : r.GetString(12),
        Kind = r.IsDBNull(13) ? null : (int)r.GetInt64(13),
    };
}
