using System.Globalization;
using Acme.Persistence;
using Acme.Securities.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Securities.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="ISecurityRepository"/>. Table <c>security</c> caches the
/// securities master data (synced from the app server). Upsert is by (code, type).
/// Replaces AlphaAgent's EF Core <c>SecurityRepository</c>.
/// </summary>
public sealed class SqliteSecurityRepository : SqliteStoreBase, ISecurityRepository
{
    private const string TableName = "security";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
        "code TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL, " +
        "exchange TEXT NOT NULL, base_code TEXT NOT NULL, updated_at TEXT NOT NULL, " +
        "UNIQUE (code, type))";
    private static readonly string Columns = "id, code, name, type, exchange, base_code, updated_at";

    public SqliteSecurityRepository(string connectionString) : base(connectionString) { }

    public static SqliteSecurityRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<List<Security>> GetAllAsync()
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName}";
        return await ReadAllAsync(cmd);
    }

    public async Task<Security?> GetByIdAsync(int id)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        var list = await ReadAllAsync(cmd);
        return list.Count == 0 ? null : list[0];
    }

    public async Task<Security?> GetByCodeAsync(string code)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE code = @code LIMIT 1";
        cmd.Parameters.AddWithValue("@code", code);
        var list = await ReadAllAsync(cmd);
        return list.Count == 0 ? null : list[0];
    }

    public async Task<List<Security>> GetByExchangeAsync(string exchange)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE exchange = @ex";
        cmd.Parameters.AddWithValue("@ex", exchange);
        return await ReadAllAsync(cmd);
    }

    public async Task<List<Security>> GetByTypeAsync(string type)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE type = @type";
        cmd.Parameters.AddWithValue("@type", type);
        return await ReadAllAsync(cmd);
    }

    public async Task<bool> ExistsAsync(string code)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(1) FROM {TableName} WHERE code = @code";
        cmd.Parameters.AddWithValue("@code", code);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture) > 0;
    }

    public async Task<Security> AddAsync(Security security) => await UpsertAsync(security);

    public async Task<Security> UpdateAsync(Security security)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {TableName} SET code=@code,name=@name,type=@type,exchange=@ex,base_code=@bc,updated_at=@u WHERE id=@id";
        AddParams(cmd, security);
        cmd.Parameters.AddWithValue("@id", security.Id);
        await cmd.ExecuteNonQueryAsync();
        return security;
    }

    public async Task DeleteAsync(int id)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Security>> SearchAsync(string keyword)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE code LIKE @kw OR name LIKE @kw";
        cmd.Parameters.AddWithValue("@kw", $"%{keyword}%");
        return await ReadAllAsync(cmd);
    }

    public Task AddRangeAsync(IEnumerable<Security> securities) => AddRangeAsync(securities, 0);

    public async Task AddRangeAsync(IEnumerable<Security> securities, int batchSize)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        foreach (var s in securities)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertSql();
            AddParams(cmd, s);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task<Security> UpsertAsync(Security security)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = UpsertSql() + "; SELECT id FROM " + TableName + " WHERE code=@code AND type=@type;";
        AddParams(cmd, security);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        return new Security(id, security.Code, security.Name, security.Type, security.Exchange, security.BaseCode, security.UpdatedAt);
    }

    private static string UpsertSql() =>
        $"INSERT INTO {TableName} (id, code, name, type, exchange, base_code, updated_at) " +
        "VALUES (@id, @code, @name, @type, @ex, @bc, @u) " +
        "ON CONFLICT(code, type) DO UPDATE SET name=excluded.name, exchange=excluded.exchange, " +
        "base_code=excluded.base_code, updated_at=excluded.updated_at";

    private static void AddParams(SqliteCommand cmd, Security s)
    {
        cmd.Parameters.AddWithValue("@id", s.Id == 0 ? DBNull.Value : (object)s.Id);
        cmd.Parameters.AddWithValue("@code", s.Code);
        cmd.Parameters.AddWithValue("@name", s.Name);
        cmd.Parameters.AddWithValue("@type", s.Type);
        cmd.Parameters.AddWithValue("@ex", s.Exchange);
        cmd.Parameters.AddWithValue("@bc", s.BaseCode);
        cmd.Parameters.AddWithValue("@u", s.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async Task<List<Security>> ReadAllAsync(SqliteCommand cmd)
    {
        var list = new List<Security>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new Security(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        return list;
    }
}
