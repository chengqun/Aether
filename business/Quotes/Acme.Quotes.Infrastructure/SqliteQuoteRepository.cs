using System.Globalization;
using Acme.Persistence;
using Acme.Quotes.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Quotes.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="IQuoteRepository"/>. Table <c>quote</c> caches K-line
/// bars keyed by (security_id, freq, date). The <c>id</c> column is autoincrement
/// and not read back (Quote entity has no Id ctor; AnalysisManager uses Date/OHLCV).
/// Replaces AlphaAgent's EF Core <c>QuoteRepository</c>.
/// </summary>
public sealed class SqliteQuoteRepository : SqliteStoreBase, IQuoteRepository
{
    private const string TableName = "quote";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY AUTOINCREMENT, security_id INTEGER NOT NULL, freq TEXT NOT NULL, " +
        "date TEXT NOT NULL, open REAL NOT NULL, high REAL NOT NULL, low REAL NOT NULL, " +
        "close REAL NOT NULL, volume REAL NOT NULL, updated_at TEXT NOT NULL, " +
        "UNIQUE (security_id, freq, date))";
    private static readonly string Columns = "security_id, freq, date, open, high, low, close, volume";

    public SqliteQuoteRepository(string connectionString) : base(connectionString) { }

    public static SqliteQuoteRepository ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public async Task<List<Quote>> GetBySecurityIdAsync(int securityId, string freq, int limit = 100)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM (SELECT {Columns} FROM {TableName} WHERE security_id=@s AND freq=@f ORDER BY date DESC LIMIT @l) ORDER BY date ASC";
        cmd.Parameters.AddWithValue("@s", securityId);
        cmd.Parameters.AddWithValue("@f", freq);
        cmd.Parameters.AddWithValue("@l", limit);
        return await ReadAllAsync(cmd);
    }

    public async Task<Quote?> GetLatestBySecurityIdAsync(int securityId, string freq)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {TableName} WHERE security_id=@s AND freq=@f ORDER BY date DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@s", securityId);
        cmd.Parameters.AddWithValue("@f", freq);
        var list = await ReadAllAsync(cmd);
        return list.Count == 0 ? null : list[0];
    }

    public async Task<Quote> AddAsync(Quote quote)
    {
        await AddRangeAsync(new[] { quote });
        return quote;
    }

    public async Task AddRangeAsync(IEnumerable<Quote> quotes)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        foreach (var q in quotes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {TableName} (security_id, freq, date, open, high, low, close, volume, updated_at) " +
                "VALUES (@s, @f, @d, @o, @h, @l, @c, @v, @u) " +
                "ON CONFLICT(security_id, freq, date) DO UPDATE SET open=excluded.open, high=excluded.high, " +
                "low=excluded.low, close=excluded.close, volume=excluded.volume, updated_at=excluded.updated_at";
            cmd.Parameters.AddWithValue("@s", q.SecurityId);
            cmd.Parameters.AddWithValue("@f", q.Freq);
            cmd.Parameters.AddWithValue("@d", q.Date.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@o", (double)q.Open);
            cmd.Parameters.AddWithValue("@h", (double)q.High);
            cmd.Parameters.AddWithValue("@l", (double)q.Low);
            cmd.Parameters.AddWithValue("@c", (double)q.Close);
            cmd.Parameters.AddWithValue("@v", (double)q.Volume);
            cmd.Parameters.AddWithValue("@u", q.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public async Task DeleteBySecurityIdAsync(int securityId, string freq)
    {
        await EnsureSchemaAsync(CreateSql);
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE security_id=@s AND freq=@f";
        cmd.Parameters.AddWithValue("@s", securityId);
        cmd.Parameters.AddWithValue("@f", freq);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task<Quote> UpdateAsync(Quote quote) => AddAsync(quote);

    private static async Task<List<Quote>> ReadAllAsync(SqliteCommand cmd)
    {
        var list = new List<Quote>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Quote(
                reader.GetInt32(0),
                DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                (decimal)reader.GetDouble(3),
                (decimal)reader.GetDouble(4),
                (decimal)reader.GetDouble(5),
                (decimal)reader.GetDouble(6),
                (decimal)reader.GetDouble(7),
                reader.GetString(1)));
        }
        return list;
    }
}
