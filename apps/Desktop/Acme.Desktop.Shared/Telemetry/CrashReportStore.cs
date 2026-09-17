using System.Globalization;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Desktop.Shared.Telemetry;

/// <summary>
/// 崩溃遥测本地队列（tenantDir/crash.db）。house style 同 <see cref="SqliteDeviceConfigRepository"/>：
/// ForFile 工厂 + per-method EnsureSchema + per-call 连接（崩溃路径任意线程可安全调）。
/// <para>
/// <b>同步 API 给崩溃路径</b>（CrashRecorder——handler 里不能有 await，且进程可能在下一行就死）；
/// <b>异步 API 给 uploader</b>（登录后批量 drain）。两路共用基类的一把 schema 锁，先到先建。
/// </para>
/// 表：<c>crash_report</c>（UNIQUE(session_id, exception_hash)——同会话同异常 upsert 计数合并）+
/// <c>session_marker</c>（会话墓碑：started 时插一行，干净退出标 clean，下次启动扫未标未处理的合成
/// UncleanShutdown 记录——原生 SIGABRT 托管层抓不到的唯一兜底）。
/// </summary>
public sealed class CrashReportStore : SqliteStoreBase
{
    private const string ReportTable = "crash_report";
    private const string MarkerTable = "session_marker";

    private const string CreateSql =
        "CREATE TABLE IF NOT EXISTS " + ReportTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "session_id TEXT NOT NULL, " +
        "exception_hash TEXT NOT NULL, " +
        "source TEXT NOT NULL, " +
        "severity INTEGER NOT NULL, " +
        "exception_type TEXT NOT NULL, " +
        "message TEXT NOT NULL, " +
        "stack TEXT NOT NULL, " +
        "count INTEGER NOT NULL DEFAULT 1, " +
        "app TEXT NOT NULL, " +
        "app_version TEXT NOT NULL, " +
        "os TEXT NOT NULL, " +
        "device_name TEXT, " +
        "occurred_at TEXT NOT NULL, " +
        "created_at TEXT NOT NULL, " +
        "attempts INTEGER NOT NULL DEFAULT 0, " +
        "UNIQUE(session_id, exception_hash)); " +
        "CREATE TABLE IF NOT EXISTS " + MarkerTable + " (" +
        "id TEXT NOT NULL PRIMARY KEY, " +
        "app TEXT NOT NULL, " +
        "started_at TEXT NOT NULL, " +
        "clean INTEGER NOT NULL DEFAULT 0, " +
        "processed INTEGER NOT NULL DEFAULT 0)";

    private const string ReportColumns =
        "id, session_id, exception_hash, source, severity, exception_type, message, stack, " +
        "count, app, app_version, os, device_name, occurred_at, created_at, attempts";

    private const int MaxAttempts = 10;
    private const int MaxAgeDays = 30;
    private const int MaxPendingRows = 500;

    public CrashReportStore(string connectionString) : base(connectionString) { }

    public static CrashReportStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    // —— 同步 API（崩溃路径） ——

    /// <summary>插入/累计一条报告：同 (session, hash) 已存在则 count+1 并刷新 message/stack/occurred_at（单语句原子 upsert）。</summary>
    public void UpsertReport(CrashReportRow row)
    {
        EnsureSchema(CreateSql);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {ReportTable} " +
            "(id, session_id, exception_hash, source, severity, exception_type, message, stack, count, app, app_version, os, device_name, occurred_at, created_at, attempts) " +
            "VALUES (@id, @sid, @hash, @src, @sev, @et, @msg, @stack, @cnt, @app, @ver, @os, @dev, @occ, @cre, 0) " +
            $"ON CONFLICT(session_id, exception_hash) DO UPDATE SET " +
            "count = count + 1, occurred_at = excluded.occurred_at, message = excluded.message, stack = excluded.stack";
        BindRow(cmd, row);
        cmd.ExecuteNonQuery();
    }

    /// <summary>写本会话墓碑（started_at = now，clean=0——走干净退出路径才置 1）。</summary>
    public void InsertSessionMarker(Guid sessionId, string app)
    {
        EnsureSchema(CreateSql);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT OR REPLACE INTO {MarkerTable} (id, app, started_at, clean, processed) " +
            "VALUES (@id, @app, @t, 0, 0)";
        cmd.Parameters.AddWithValue("@id", sessionId.ToString());
        cmd.Parameters.AddWithValue("@app", app);
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    /// <summary>标记本会话干净退出（MarkCleanShutdown 调用；未标记的下次启动会合成墓碑记录）。</summary>
    public void MarkSessionClean(Guid sessionId)
    {
        EnsureSchema(CreateSql);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {MarkerTable} SET clean = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>墓碑扫描：未干净退出且尚未处理过的旧会话。</summary>
    public List<UncleanSession> FindUnprocessedUncleanSessions()
    {
        EnsureSchema(CreateSql);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, app, started_at FROM {MarkerTable} WHERE clean = 0 AND processed = 0";
        using var reader = cmd.ExecuteReader();
        var list = new List<UncleanSession>();
        while (reader.Read())
        {
            list.Add(new UncleanSession(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }
        return list;
    }

    /// <summary>墓碑处理完标记（防下次启动重复合成）。</summary>
    public void MarkSessionsProcessed(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        EnsureSchema(CreateSql);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {MarkerTable} SET processed = 1 WHERE id IN ({string.Join(",", idList.Select((_, i) => $"@p{i}"))})";
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", idList[i].ToString());
        cmd.ExecuteNonQuery();
    }

    // —— 异步 API（uploader drain 用） ——

    /// <summary>取最旧的 pending 批（created_at ASC——先进先上报）。</summary>
    public async Task<List<CrashReportRow>> GetPendingAsync(int limit, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {ReportColumns} FROM {ReportTable} ORDER BY created_at ASC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<CrashReportRow>();
        while (await reader.ReadAsync(ct))
            list.Add(ReadRow(reader));
        return list;
    }

    /// <summary>上报成功删行（行本身即"待上报"状态，删了即确认）。</summary>
    public async Task DeleteReportsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {ReportTable} WHERE id IN ({string.Join(",", idList.Select((_, i) => $"@p{i}"))})";
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", idList[i].ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>上报失败计一次尝试（超上限的行由 PruneAsync 清掉）。</summary>
    public async Task IncrementAttemptsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {ReportTable} SET attempts = attempts + 1 WHERE id IN ({string.Join(",", idList.Select((_, i) => $"@p{i}"))})";
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", idList[i].ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>队列清理：删尝试超限（10 次）/ 超龄（30 天）的行；pending 超过 500 时按新留旧。</summary>
    public async Task PruneAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-MaxAgeDays).ToString("O", CultureInfo.InvariantCulture);
        await using (var cmd1 = conn.CreateCommand())
        {
            cmd1.CommandText = $"DELETE FROM {ReportTable} WHERE attempts >= @max OR created_at < @cutoff";
            cmd1.Parameters.AddWithValue("@max", MaxAttempts);
            cmd1.Parameters.AddWithValue("@cutoff", cutoff);
            await cmd1.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText =
                $"DELETE FROM {ReportTable} WHERE id NOT IN " +
                $"(SELECT id FROM {ReportTable} ORDER BY created_at DESC LIMIT @cap)";
            cmd2.Parameters.AddWithValue("@cap", MaxPendingRows);
            await cmd2.ExecuteNonQueryAsync(ct);
        }
    }

    private static void BindRow(SqliteCommand cmd, CrashReportRow row)
    {
        cmd.Parameters.AddWithValue("@id", row.Id.ToString());
        cmd.Parameters.AddWithValue("@sid", row.SessionId.ToString());
        cmd.Parameters.AddWithValue("@hash", row.ExceptionHash);
        cmd.Parameters.AddWithValue("@src", row.Source);
        cmd.Parameters.AddWithValue("@sev", (int)row.Severity);
        cmd.Parameters.AddWithValue("@et", row.ExceptionType);
        cmd.Parameters.AddWithValue("@msg", row.Message);
        cmd.Parameters.AddWithValue("@stack", row.Stack);
        cmd.Parameters.AddWithValue("@cnt", row.Count < 1 ? 1 : row.Count);
        cmd.Parameters.AddWithValue("@app", row.App);
        cmd.Parameters.AddWithValue("@ver", row.AppVersion);
        cmd.Parameters.AddWithValue("@os", row.Os);
        cmd.Parameters.AddWithValue("@dev", (object?)row.DeviceName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@occ", row.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@cre", row.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    private static CrashReportRow ReadRow(SqliteDataReader reader)
    {
        return new CrashReportRow
        {
            Id = Guid.Parse(reader.GetString(0)),
            SessionId = Guid.Parse(reader.GetString(1)),
            ExceptionHash = reader.GetString(2),
            Source = reader.GetString(3),
            Severity = reader.GetInt64(4) == 0 ? CrashSeverity.Fatal : CrashSeverity.NonFatal,
            ExceptionType = reader.GetString(5),
            Message = reader.GetString(6),
            Stack = reader.GetString(7),
            Count = (int)reader.GetInt64(8),
            App = reader.GetString(9),
            AppVersion = reader.GetString(10),
            Os = reader.GetString(11),
            DeviceName = reader.IsDBNull(12) ? null : reader.GetString(12),
            OccurredAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            Attempts = (int)reader.GetInt64(15),
        };
    }
}
