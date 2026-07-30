using System.Globalization;
using System.IO;
using Acme.Notes.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Notes.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="INoteRepository"/>. Table <c>note_node</c> stores the
/// tree-structured note nodes. Single-user (no user_id column). WAL + busy_timeout.
/// </summary>
public sealed class SqliteNoteRepository : INoteRepository
{
    private const string TableName = "note_node";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, parent_id TEXT, sort_order INTEGER NOT NULL, " +
        "title TEXT NOT NULL, content TEXT NOT NULL, syntax TEXT, tags TEXT, " +
        "is_readonly INTEGER NOT NULL, node_type INTEGER NOT NULL, linked_id TEXT, " +
        "source TEXT, marketplace_id TEXT, marketplace_synced_at TEXT, display_name TEXT, icon TEXT, " +
        "created_at TEXT NOT NULL, updated_at TEXT NOT NULL)";

    private static readonly string Cols = "id, parent_id, sort_order, title, content, syntax, tags, is_readonly, node_type, linked_id, source, marketplace_id, marketplace_synced_at, display_name, icon, created_at, updated_at";

    private readonly string _cs;
    private int _init;

    public SqliteNoteRepository(string connectionString)
        => _cs = !string.IsNullOrWhiteSpace(connectionString) ? connectionString : throw new ArgumentException("required", nameof(connectionString));

    public static SqliteNoteRepository ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<List<NoteNode>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {TableName} ORDER BY sort_order";
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<NoteNode?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {TableName} WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        var list = await ReadAllAsync(cmd, ct);
        return list.Count == 0 ? null : list[0];
    }

    public async Task<List<NoteNode>> GetChildrenAsync(Guid? parentId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = parentId is null
            ? $"SELECT {Cols} FROM {TableName} WHERE parent_id IS NULL ORDER BY sort_order"
            : $"SELECT {Cols} FROM {TableName} WHERE parent_id=@p ORDER BY sort_order";
        if (parentId is not null) cmd.Parameters.AddWithValue("@p", parentId.Value.ToString());
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<NoteNode> CreateAsync(NoteNode node, CancellationToken ct = default)
    {
        if (node.Id == Guid.Empty) node.Id = Guid.NewGuid();
        node.CreatedAt = node.UpdatedAt = DateTimeOffset.UtcNow;
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {TableName} ({Cols}) VALUES (@id,@pid,@so,@t,@cnt,@syn,@tg,@ro,@nt,@lid,@src,@mid,@msa,@dn,@icon,@ca,@ua)";
        AddParams(cmd, node);
        await cmd.ExecuteNonQueryAsync(ct);
        return node;
    }

    public async Task UpdateAsync(NoteNode node, CancellationToken ct = default)
    {
        node.Touch();
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"UPDATE {TableName} SET parent_id=@pid, sort_order=@so, title=@t, content=@cnt, syntax=@syn, tags=@tg, is_readonly=@ro, node_type=@nt, linked_id=@lid, source=@src, marketplace_id=@mid, marketplace_synced_at=@msa, display_name=@dn, icon=@icon, updated_at=@ua WHERE id=@id";
        AddParams(cmd, node);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        // Children promoted to root (parent_id = NULL)
        await using (var p = c.CreateCommand()) { p.Transaction = tx; p.CommandText = $"UPDATE {TableName} SET parent_id=NULL WHERE parent_id=@id"; p.Parameters.AddWithValue("@id", id.ToString()); await p.ExecuteNonQueryAsync(ct); }
        await using (var d = c.CreateCommand()) { d.Transaction = tx; d.CommandText = $"DELETE FROM {TableName} WHERE id=@id"; d.Parameters.AddWithValue("@id", id.ToString()); await d.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    public async Task<List<NoteNode>> SearchAsync(string query, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var c = await OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM {TableName} WHERE title LIKE @q OR content LIKE @q OR tags LIKE @q ORDER BY updated_at DESC";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        return await ReadAllAsync(cmd, ct);
    }

    private static void AddParams(SqliteCommand cmd, NoteNode n)
    {
        cmd.Parameters.AddWithValue("@id", n.Id.ToString());
        cmd.Parameters.AddWithValue("@pid", n.ParentId.HasValue ? (object)n.ParentId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@so", n.SortOrder);
        cmd.Parameters.AddWithValue("@t", n.Title);
        cmd.Parameters.AddWithValue("@cnt", n.Content);
        cmd.Parameters.AddWithValue("@syn", (object?)n.Syntax ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tg", (object?)n.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ro", n.IsReadOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("@nt", (int)n.NodeType);
        cmd.Parameters.AddWithValue("@lid", n.LinkedId.HasValue ? (object)n.LinkedId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@src", (object?)n.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mid", n.MarketplaceId.HasValue ? (object)n.MarketplaceId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("@msa", n.MarketplaceSyncedAt.HasValue ? (object)n.MarketplaceSyncedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@dn", (object?)n.DisplayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@icon", (object?)n.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", n.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ua", n.UpdatedAt.ToString("O"));
    }

    private static async Task<List<NoteNode>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<NoteNode>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NoteNode
            {
                Id = Guid.Parse(r.GetString(0)),
                ParentId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                SortOrder = r.GetInt32(2),
                Title = r.GetString(3),
                Content = r.GetString(4),
                Syntax = r.IsDBNull(5) ? null : r.GetString(5),
                Tags = r.IsDBNull(6) ? null : r.GetString(6),
                IsReadOnly = r.GetInt32(7) != 0,
                NodeType = (NoteNodeType)r.GetInt32(8),
                LinkedId = r.IsDBNull(9) ? null : Guid.Parse(r.GetString(9)),
                Source = r.IsDBNull(10) ? null : r.GetString(10),
                MarketplaceId = r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)),
                MarketplaceSyncedAt = r.IsDBNull(12) ? null : DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture),
                DisplayName = r.IsDBNull(13) ? null : r.GetString(13),
                Icon = r.IsDBNull(14) ? null : r.GetString(14),
                CreatedAt = DateTimeOffset.Parse(r.GetString(15), CultureInfo.InvariantCulture),
                UpdatedAt = DateTimeOffset.Parse(r.GetString(16), CultureInfo.InvariantCulture)
            });
        }
        return list;
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _init, 1, 0) != 0) return;
        try
        {
            var dbPath = ExtractPath();
            if (!string.IsNullOrEmpty(dbPath)) { var dir = Path.GetDirectoryName(dbPath); if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            await using var c = await OpenAsync(ct);
            await using (var wal = c.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; await wal.ExecuteNonQueryAsync(ct); }
            await using var cmd = c.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync(ct);
            // 老库迁移：note_node 早期无 icon 列，按需补列（已存在则忽略）。
            await MigrateAddColumnAsync(c, "icon", "TEXT", ct);
        }
        catch { Interlocked.Exchange(ref _init, 0); throw; }
    }

    /// <summary>给老库补列：ADD COLUMN 在列已存在时抛异常，吞掉即可（幂等迁移）。</summary>
    private static async Task MigrateAddColumnAsync(SqliteConnection c, string column, string type, CancellationToken ct)
    {
        try
        {
            await using var m = c.CreateCommand();
            m.CommandText = $"ALTER TABLE {TableName} ADD COLUMN {column} {type}";
            await m.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // 列已存在 — 忽略。
        }
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
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            { var val = part[(eq + 1)..].Trim(); return val.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ? null : val; }
        }
        return null;
    }
}
