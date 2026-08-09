using System.Globalization;
using Acme.Persistence;
using Acme.ServiceAccount.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.ServiceAccount.Infrastructure;

public sealed class SqliteServiceAccountPostCacheRepository : SqliteStoreBase, IServiceAccountPostCacheRepository
{
    private const string TableName = "service_account_post";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "service_account_id TEXT NOT NULL, " +
        "title TEXT NOT NULL, " +
        "summary TEXT, " +
        "cover_image_url TEXT, " +
        "content_type TEXT NOT NULL, " +
        "is_pinned INTEGER NOT NULL, " +
        "published_at TEXT NOT NULL, " +
        "service_account_name TEXT NOT NULL)";

    private static readonly string SelectColumns =
        "id, service_account_id, title, summary, cover_image_url, content_type, is_pinned, published_at, service_account_name";

    public SqliteServiceAccountPostCacheRepository(string connectionString) : base(connectionString) { }

    public static SqliteServiceAccountPostCacheRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<List<ServiceAccountPostCacheItem>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} ORDER BY is_pinned DESC, published_at DESC";
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<List<ServiceAccountPostCacheItem>> GetByServiceAccountIdAsync(Guid serviceAccountId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM {TableName} WHERE service_account_id = @sid ORDER BY is_pinned DESC, published_at DESC";
        cmd.Parameters.AddWithValue("@sid", serviceAccountId.ToString());
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<DateTime?> GetLatestPublishedAtAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(published_at) FROM " + TableName;
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        return DateTime.Parse(result.ToString()!, CultureInfo.InvariantCulture);
    }

    public async Task<DateTime?> GetLatestPublishedAtAsync(Guid serviceAccountId, CancellationToken ct = default)
    {
        // 按账号隔离 since 游标：之前用全局 MAX，多服务号切换时较旧账号的新文章被全局 MAX 过滤掉，永远同步不到。
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(published_at) FROM " + TableName + " WHERE service_account_id = @sid";
        cmd.Parameters.AddWithValue("@sid", serviceAccountId.ToString());
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull) return null;
        return DateTime.Parse(result.ToString()!, CultureInfo.InvariantCulture);
    }

    public async Task AddOrUpdateAsync(IEnumerable<ServiceAccountPostCacheItem> items, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var item in items)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT OR REPLACE INTO {TableName} " +
                "(id, service_account_id, title, summary, cover_image_url, content_type, is_pinned, published_at, service_account_name) " +
                "VALUES (@id, @sid, @t, @s, @c, @ct, @p, @pa, @san)";
            cmd.Parameters.AddWithValue("@id", item.Id.ToString());
            cmd.Parameters.AddWithValue("@sid", item.ServiceAccountId.ToString());
            cmd.Parameters.AddWithValue("@t", item.Title);
            cmd.Parameters.AddWithValue("@s", item.Summary ?? string.Empty);
            cmd.Parameters.AddWithValue("@c", item.CoverImageUrl ?? string.Empty);
            cmd.Parameters.AddWithValue("@ct", item.ContentType);
            cmd.Parameters.AddWithValue("@p", item.IsPinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@pa", item.PublishedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@san", item.ServiceAccountName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task AddAsync(ServiceAccountPostCacheItem item, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {TableName} " +
            "(id, service_account_id, title, summary, cover_image_url, content_type, is_pinned, published_at, service_account_name) " +
            "VALUES (@id, @sid, @t, @s, @c, @ct, @p, @pa, @san)";
        cmd.Parameters.AddWithValue("@id", item.Id.ToString());
        cmd.Parameters.AddWithValue("@sid", item.ServiceAccountId.ToString());
        cmd.Parameters.AddWithValue("@t", item.Title);
        cmd.Parameters.AddWithValue("@s", item.Summary ?? string.Empty);
        cmd.Parameters.AddWithValue("@c", item.CoverImageUrl ?? string.Empty);
        cmd.Parameters.AddWithValue("@ct", item.ContentType);
        cmd.Parameters.AddWithValue("@p", item.IsPinned ? 1 : 0);
        cmd.Parameters.AddWithValue("@pa", item.PublishedAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@san", item.ServiceAccountName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName}";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<ServiceAccountPostCacheItem>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<ServiceAccountPostCacheItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ServiceAccountPostCacheItem
            {
                Id = Guid.Parse(reader.GetString(0)),
                ServiceAccountId = Guid.Parse(reader.GetString(1)),
                Title = reader.GetString(2),
                Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
                CoverImageUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                ContentType = reader.GetString(5),
                IsPinned = reader.GetInt64(6) != 0,
                PublishedAt = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                ServiceAccountName = reader.GetString(8)
            });
        }
        return list;
    }
}
