using System.Globalization;
using System.IO;
using Acme.Quotes.Domain;
using Microsoft.Data.Sqlite;

namespace Acme.Quotes.Infrastructure;

/// <summary>
/// SQLite-backed <see cref="IQuoteRepository"/>. Table <c>quote</c> caches K-line
/// bars keyed by (security_id, freq, date). The <c>id</c> column is autoincrement
/// and not read back (Quote entity has no Id ctor; AnalysisManager uses Date/OHLCV).
/// Replaces AlphaAgent's EF Core <c>QuoteRepository</c>.
/// </summary>
public sealed class SqliteQuoteRepository : IQuoteRepository
{
    private const string TableName = "quote";
    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + TableName + " (" +
        "id INTEGER PRIMARY KEY AUTOINCREMENT, security_id INTEGER NOT NULL, freq TEXT NOT NULL, " +
        "date TEXT NOT NULL, open REAL NOT NULL, high REAL NOT NULL, low REAL NOT NULL, " +
        "close REAL NOT NULL, volume REAL NOT NULL, updated_at TEXT NOT NULL, " +
        "UNIQUE (security_id, freq, date))";
    private static readonly string Columns = "security_id, freq, date, open, high, low, close, volume";

    private readonly string _connectionString;
    private int _initialized;

    public SqliteQuoteRepository(string connectionString)
        => _connectionString = !string.IsNullOrWhiteSpace(connectionString) ? connectionString : throw new ArgumentException("required", nameof(connectionString));

    public static SqliteQuoteRepository ForFile(string dbPath) => new($"Data Source={dbPath}");

    public async Task<List<Quote>> GetBySecurityIdAsync(int securityId, string freq, int limit = 100)
    {
        await EnsureSchemaAsync();
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
        await EnsureSchemaAsync();
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
        await EnsureSchemaAsync();
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
        await EnsureSchemaAsync();
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
                DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                (decimal)reader.GetDouble(3),
                (decimal)reader.GetDouble(4),
                (decimal)reader.GetDouble(5),
                (decimal)reader.GetDouble(6),
                (decimal)reader.GetDouble(7),
                reader.GetString(2)));
        }
        return list;
    }

    private async Task EnsureSchemaAsync()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;
        try
        {
            var dbPath = ExtractDataSourcePath();
            if (!string.IsNullOrEmpty(dbPath))
            {
                var dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            await using var conn = await OpenAsync();
            await using (var wal = conn.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode=WAL;"; await wal.ExecuteNonQueryAsync(); }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = CreateSql;
            await cmd.ExecuteNonQueryAsync();
        }
        catch { Interlocked.Exchange(ref _initialized, 0); throw; }
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var p = conn.CreateCommand();
        p.CommandText = "PRAGMA busy_timeout=5000;";
        await p.ExecuteNonQueryAsync();
        return conn;
    }

    private string? ExtractDataSourcePath()
    {
        foreach (var part in _connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var key = part[..eq].Trim();
            if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) || key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
            {
                var val = part[(eq + 1)..].Trim();
                return val.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ? null : val;
            }
        }
        return null;
    }
}
