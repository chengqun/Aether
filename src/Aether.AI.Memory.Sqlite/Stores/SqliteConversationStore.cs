using System.Globalization;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Messages;
using Microsoft.Data.Sqlite;

namespace Aether.AI.Memory.Stores;

/// <summary>
/// SQLite-backed <see cref="IConversationStore"/>. Tables <c>conversation</c> +
/// <c>conversation_message</c>. Single-user (user_id column kept for the abstraction
/// but the desktop passes empty). WAL + busy_timeout, lazy schema. Mirrors
/// <c>SqliteNoteRepository</c>'s pattern (no EF Core).
/// </summary>
public sealed class SqliteConversationStore : IConversationStore
{
    private const string ConvTable = "conversation";
    private const string MsgTable = "conversation_message";

    private const string CreateConvSql =
        "CREATE TABLE IF NOT EXISTS " + ConvTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, user_id TEXT, agent_id TEXT NOT NULL, agent_name TEXT NOT NULL, " +
        "title TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)";

    private const string CreateMsgSql =
        "CREATE TABLE IF NOT EXISTS " + MsgTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, conversation_id TEXT NOT NULL, seq INTEGER NOT NULL, " +
        "role INTEGER NOT NULL, content TEXT NOT NULL, tool_name TEXT, tool_call_id TEXT, metadata TEXT, created_at TEXT NOT NULL, " +
        "FOREIGN KEY(conversation_id) REFERENCES " + ConvTable + "(id) ON DELETE CASCADE)";

    private const string CreateConvUpdatedIdx =
        "CREATE INDEX IF NOT EXISTS idx_conversation_updated ON " + ConvTable + "(updated_at DESC)";
    private const string CreateMsgConvIdx =
        "CREATE INDEX IF NOT EXISTS idx_message_conv ON " + MsgTable + "(conversation_id, seq)";

    private readonly string _cs;
    private int _init;

    public SqliteConversationStore(string connectionString)
        => _cs = !string.IsNullOrWhiteSpace(connectionString) ? connectionString : throw new ArgumentException("required", nameof(connectionString));

    public static SqliteConversationStore ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        if (conversation is null) throw new ArgumentNullException(nameof(conversation));
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await EnsureAsync(cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);

        // upsert conversation row
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText =
                "INSERT INTO " + ConvTable + " (id,user_id,agent_id,agent_name,title,created_at,updated_at) " +
                "VALUES (@id,@uid,@aid,@an,@t,@ca,@ua) " +
                "ON CONFLICT(id) DO UPDATE SET user_id=@uid, agent_id=@aid, agent_name=@an, title=@t, updated_at=@ua";
            cmd.Parameters.AddWithValue("@id", conversation.ConversationId.ToString());
            cmd.Parameters.AddWithValue("@uid", (object?)conversation.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aid", conversation.AgentId ?? string.Empty);
            cmd.Parameters.AddWithValue("@an", conversation.AgentName ?? string.Empty);
            cmd.Parameters.AddWithValue("@t", conversation.Title ?? string.Empty);
            cmd.Parameters.AddWithValue("@ca", Format(conversation.CreatedAt));
            cmd.Parameters.AddWithValue("@ua", Format(conversation.UpdatedAt));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // replace all messages (simple: delete + reinsert). Transcripts are small.
        await using (var del = c.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM " + MsgTable + " WHERE conversation_id=@cid";
            del.Parameters.AddWithValue("@cid", conversation.ConversationId.ToString());
            await del.ExecuteNonQueryAsync(cancellationToken);
        }

        int seq = 0;
        foreach (var m in conversation.Messages)
        {
            await using var ins = c.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText =
                "INSERT INTO " + MsgTable + " (id,conversation_id,seq,role,content,tool_name,tool_call_id,metadata,created_at) " +
                "VALUES (@id,@cid,@seq,@r,@ct,@tn,@tc,@md,@ca)";
            ins.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            ins.Parameters.AddWithValue("@cid", conversation.ConversationId.ToString());
            ins.Parameters.AddWithValue("@seq", seq++);
            ins.Parameters.AddWithValue("@r", (int)m.Role);
            ins.Parameters.AddWithValue("@ct", m.Content ?? string.Empty);
            ins.Parameters.AddWithValue("@tn", (object?)m.ToolName ?? DBNull.Value);
            ins.Parameters.AddWithValue("@tc", (object?)m.ToolCallId ?? DBNull.Value);
            var md = MetadataToJson(m.Metadata);
            ins.Parameters.AddWithValue("@md", string.IsNullOrEmpty(md) ? DBNull.Value : (object)md);
            ins.Parameters.AddWithValue("@ca", Format(m.CreatedAt));
            await ins.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<Conversation?> GetAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        var conv = await ReadConversationAsync(c, conversationId, cancellationToken);
        if (conv is null) return null;
        conv.Messages.AddRange(await ReadMessagesAsync(c, conversationId, cancellationToken));
        return conv;
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT c.id, c.title, c.agent_id, c.agent_name, c.updated_at, " +
            "(SELECT content FROM " + MsgTable + " m WHERE m.conversation_id=c.id ORDER BY m.seq DESC LIMIT 1) AS preview " +
            "FROM " + ConvTable + " c " +
            "WHERE @uid IS NULL OR c.user_id IS NULL OR c.user_id=@uid " +
            "ORDER BY c.updated_at DESC";
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);

        var list = new List<ConversationSummary>();
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            list.Add(new ConversationSummary(
                Guid.Parse(r.GetString(0)),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                Parse(r.GetString(4)),
                r.IsDBNull(5) ? string.Empty : r.GetString(5)));
        }
        return list;
    }

    public async Task<IReadOnlyList<Conversation>> FindByUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM " + ConvTable + " WHERE @uid IS NULL OR user_id IS NULL OR user_id=@uid ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("@uid", (object?)userId ?? DBNull.Value);

        var ids = new List<Guid>();
        await using (var r = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(r.GetString(0)));
        }

        var list = new List<Conversation>();
        foreach (var id in ids)
        {
            var conv = await ReadConversationAsync(c, id, cancellationToken);
            if (conv is null) continue;
            conv.Messages.AddRange(await ReadMessagesAsync(c, id, cancellationToken));
            list.Add(conv);
        }
        return list;
    }

    public async Task<bool> DeleteAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        await using var c = await OpenAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(cancellationToken);
        await using (var dm = c.CreateCommand())
        {
            dm.Transaction = (SqliteTransaction)tx;
            // FK ON DELETE CASCADE requires PRAGMA foreign_keys=ON; set it to be safe.
            dm.CommandText = "DELETE FROM " + MsgTable + " WHERE conversation_id=@cid";
            dm.Parameters.AddWithValue("@cid", conversationId.ToString());
            await dm.ExecuteNonQueryAsync(cancellationToken);
        }
        int affected;
        await using (var dc = c.CreateCommand())
        {
            dc.Transaction = (SqliteTransaction)tx;
            dc.CommandText = "DELETE FROM " + ConvTable + " WHERE id=@id";
            dc.Parameters.AddWithValue("@id", conversationId.ToString());
            affected = await dc.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return affected > 0;
    }

    private static async Task<Conversation?> ReadConversationAsync(SqliteConnection c, Guid id, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,user_id,agent_id,agent_name,title,created_at,updated_at FROM " + ConvTable + " WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Conversation
        {
            ConversationId = Guid.Parse(r.GetString(0)),
            UserId = r.IsDBNull(1) ? null : r.GetString(1),
            AgentId = r.GetString(2),
            AgentName = r.GetString(3),
            Title = r.GetString(4),
            CreatedAt = Parse(r.GetString(5)),
            UpdatedAt = Parse(r.GetString(6)),
        };
    }

    private static async Task<List<Message>> ReadMessagesAsync(SqliteConnection c, Guid conversationId, CancellationToken ct)
    {
        var list = new List<Message>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT role,content,tool_name,tool_call_id,metadata,created_at FROM " + MsgTable + " WHERE conversation_id=@cid ORDER BY seq";
        cmd.Parameters.AddWithValue("@cid", conversationId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new Message
            {
                Role = (MessageRole)r.GetInt32(0),
                Content = r.GetString(1),
                ToolName = r.IsDBNull(2) ? null : r.GetString(2),
                ToolCallId = r.IsDBNull(3) ? null : r.GetString(3),
                Metadata = r.IsDBNull(4) ? null : MetadataFromJson(r.GetString(4)),
                CreatedAt = Parse(r.GetString(5)),
            });
        }
        return list;
    }

    private static string? MetadataToJson(IReadOnlyDictionary<string, string>? meta)
        => meta is null || meta.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(meta);

    private static IReadOnlyDictionary<string, string>? MetadataFromJson(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch { return null; }
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _init, 1, 0) != 0) return;
        try
        {
            var dbPath = ExtractPath();
            if (!string.IsNullOrEmpty(dbPath)) { var dir = Path.GetDirectoryName(dbPath); if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            await using var c = await OpenAsync(ct);
            await using (var fk = c.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys=ON;"; await fk.ExecuteNonQueryAsync(ct); }
            await using (var wal = c.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; await wal.ExecuteNonQueryAsync(ct); }
            await using var cmd = c.CreateCommand();
            cmd.CommandText = CreateConvSql + ";" + CreateMsgSql + ";" + CreateConvUpdatedIdx + ";" + CreateMsgConvIdx;
            await cmd.ExecuteNonQueryAsync(ct);

            // 迁移：旧库的 message 表可能没有 metadata 列（用于持久化工具步 input/output、思考段标记等）。ALTER 幂等（列已存在则忽略）。
            await using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE " + MsgTable + " ADD COLUMN metadata TEXT";
            try { await alter.ExecuteNonQueryAsync(ct); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* 列已存在，忽略 */ }
        }
        catch { Interlocked.Exchange(ref _init, 0); throw; }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_cs);
        await c.OpenAsync(ct);
        await using var p = c.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000;";
        await p.ExecuteNonQueryAsync(ct);
        return c;
    }

    private string? ExtractPath()
    {
        foreach (var part in _cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                return part["Data Source=".Length..];
        }
        return null;
    }

    private static string Format(DateTimeOffset dto) => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
