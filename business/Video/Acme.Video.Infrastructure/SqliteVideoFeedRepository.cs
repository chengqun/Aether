using System.Globalization;
using Acme.Persistence;
using Acme.Video.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Video.Infrastructure;

public sealed class SqliteVideoFeedRepository : SqliteStoreBase, IVideoFeedRepository
{
    private const string TableName = "video_feed";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "title TEXT NOT NULL, " +
        "video_url TEXT NOT NULL, " +
        "cover_url TEXT, " +
        "author TEXT NOT NULL, " +
        "duration INTEGER NOT NULL, " +
        "created_at TEXT NOT NULL)";

    private static readonly string SelectColumns =
        "id, title, video_url, cover_url, author, duration, created_at";

    public SqliteVideoFeedRepository(string connectionString) : base(connectionString) { }

    public static SqliteVideoFeedRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    /// <summary>
    /// 幂等种子：首次建表后插入 2 条示例视频（m3u8 直链），保证视频号页有内容可播。
    /// INSERT OR IGNORE + 固定 Guid 主键 → 重复执行不报错、不重复。
    /// 对齐 AlphaAgent VideoFeedData 的两条种子（聚宝盆 / 修仙不如干饭）。
    /// </summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        await SeedAsync(connection, ct).ConfigureAwait(false);
    }

    private static async Task SeedAsync(SqliteConnection connection, CancellationToken ct)
    {
        // 固定 Guid（幂等主键），与 VideoFeedData.All 对齐。
        var seeds = new[]
        {
            (Id: Guid.Parse("d1c10000-0000-0000-0000-000000000001"), Title: "聚宝盆",
             Url: "https://vodcnd01.myqqdd.com/20260511/noPZHlna/2619kb/hls/index.m3u8"),
            (Id: Guid.Parse("d1c10000-0000-0000-0000-000000000002"), Title: "修仙不如干饭",
             Url: "https://play.modujx11.com/20260502/5EQ8k4ac/index.m3u8"),
        };

        foreach (var s in seeds)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText =
                $"INSERT OR IGNORE INTO {TableName} (id, title, video_url, cover_url, author, duration, created_at) " +
                "VALUES (@id, @t, @v, @c, @a, @d, @ca)";
            cmd.Parameters.AddWithValue("@id", s.Id.ToString());
            cmd.Parameters.AddWithValue("@t", s.Title);
            cmd.Parameters.AddWithValue("@v", s.Url);
            cmd.Parameters.AddWithValue("@c", string.Empty);
            cmd.Parameters.AddWithValue("@a", "视频号推荐");
            cmd.Parameters.AddWithValue("@d", 0);
            cmd.Parameters.AddWithValue("@ca", "2026-01-01T00:00:00Z");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<List<VideoItem>> GetPagedAsync(int limit, int offset, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} ORDER BY created_at DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<VideoItem> AddAsync(VideoItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {TableName} (id, title, video_url, cover_url, author, duration, created_at) " +
            "VALUES (@id, @t, @v, @c, @a, @d, @ca)";
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@t", item.Title);
        cmd.Parameters.AddWithValue("@v", item.VideoUrl);
        cmd.Parameters.AddWithValue("@c", item.CoverUrl ?? string.Empty);
        cmd.Parameters.AddWithValue("@a", item.Author);
        cmd.Parameters.AddWithValue("@d", item.Duration);
        cmd.Parameters.AddWithValue("@ca", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
        return item;
    }

    private static async Task<List<VideoItem>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<VideoItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new VideoItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                VideoUrl = reader.GetString(2),
                CoverUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                Author = reader.GetString(4),
                Duration = reader.GetInt32(5),
                CreatedAt = DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture)
            });
        }
        return list;
    }
}
